# Wayland RDP on Hyper-V: Research & Implementation Notes

## Overview

This document captures all findings from implementing Wayland-native RDP on
Hyper-V Linux VMs using [Lamco RDP Server](https://github.com/lamco-admin/lamco-rdp-server)
as an alternative to xrdp. The core challenge is that Hyper-V's `hyperv_drm`
driver is a software framebuffer with no GPU render node, which breaks
DMA-BUF-based screen capture on KDE/KWin.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  Windows Host (mstsc)                                   │
│  ┌───────────┐     ┌──────────────────┐                  │
│  │ RDP Client│◄───►│ Lamco RDP Server │  port 3389       │
│  └───────────┘ TLS │ (Rust, IronRDP)  │                  │
└─────────────────────┴──┬──────────────┴──────────────────┘
                          │ D-Bus + PipeWire
┌─────────────────────────┴───────────────────────────────┐
│  Linux Guest (Hyper-V VM)                                │
│  ┌────────────┐  ┌─────────────┐  ┌────────────────────┐ │
│  │ Compositor │  │ xdg-desktop │  │ PipeWire           │ │
│  │ (KWin or   │─►│ -portal     │─►│ ScreenCast stream  │ │
│  │  mutter)   │  │ (KDE/GNOME) │  │ (MemFd or DMA-BUF) │ │
│  └─────┬──────┘  └─────────────┘  └────────────────────┘ │
│        │ Wayland                                       │
│  ┌─────┴──────────────────────────────────────────────┐ │
│  │ hyperv_drm (DRM driver, /dev/dri/card0)             │ │
│  │ Render node (/dev/dri/renderD128) — patched         │ │
│  │ GEM shmem → CPU blit to VMBus VRAM                  │ │
│  └────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

## The Core Problem: No GPU Render Node

### hyperv_drm driver limitations

The `hyperv_drm` kernel driver (`drivers/gpu/drm/hyperv/`) is a **display-only**
software framebuffer driver. Key facts:

- **No `DRIVER_RENDER` feature bit** → no `/dev/dri/renderD128` render node
- Uses `DRM_GEM_SHMEM_DRIVER_OPS` → GEM objects in system memory (shmem)
- CPU `drm_fb_memcpy` blits shmem to VMBus VRAM aperture
- No DMA-BUF export, no GBM/EGL hardware acceleration
- Registered as `DRIVER_MODESET | DRIVER_GEM | DRIVER_ATOMIC` (no RENDER)

### Upstream status (as of kernel 7.0.x, August 2026)

Recent upstream commits are all maintenance/hardening:
- Packet validation, `drm_panic` support, atomic-helper refactoring
- **Zero commits** add render nodes, DMA-BUF, or GPU acceleration
- Microsoft's WSL2-Linux-Kernel has identical `hyperv_drm` — no proprietary patches
- `dxgkrnl` exists for WSL2 DirectX compute but is NOT a DRM driver

## Compositor Comparison on Hyper-V

### GNOME/mutter — WORKS ✅

- Uses `BufferType::MemFd` (shared memory) for PipeWire ScreenCast
- Does NOT require a GPU render node or DMA-BUF
- Captures frames from the compositor's own rendering buffer via shm
- Works with `hyperv_drm` out of the box (no patches needed)
- Resolution forced via `monitors.xml` (hyperv_drm defaults to 1024x768)

### KDE/KWin — DOES NOT WORK (stock) ❌

- KWin's ScreenCast pipeline uses DMA-BUF buffers for zero-copy capture
- Requires `/dev/dri/renderD128` for GBM/EGL buffer allocation
- `drmGetDeviceFromDevId()` fails on hyperv_drm (VMBus device, not PCI)
- PipeWire stream stuck in infinite "format negotiated" loop, no buffers allocated
- KWin falls back to QPainter compositor (no OpenGL) without EGL
- Error: `No render node have been found, not initializing wl-drm`

### KDE/KWin — PARTIALLY WORKS (patched) ⚠️

With `LIBGL_ALWAYS_SOFTWARE=1` (no `MESA_LOADER_DRIVER_OVERRIDE`):
- EGL on GBM with card0 (hyperv_drm) succeeds → `eglInitialize: 1`
- Compositing type becomes `gl2` (OpenGL software rendering)
- BUT: ScreenCast still fails because KWin can't advertise `linux-dmabuf`
  without a render node (`drmGetDeviceFromDevId()` fails)

## The Fix: Two Patches (kernel module + libdrm)

### Patch 1: hyperv_drm kernel module — DRIVER_RENDER

### One-line kernel module patch

```c
// File: drivers/gpu/drm/hyperv/hyperv_drm_drv.c

// Before:
static struct drm_driver hyperv_driver = {
    .driver_features = DRIVER_MODESET | DRIVER_GEM | DRIVER_ATOMIC,

// After:
static struct drm_driver hyperv_driver = {
    .driver_features = DRIVER_MODESET | DRIVER_GEM | DRIVER_ATOMIC | DRIVER_RENDER,
```

Adding `DRIVER_RENDER` tells the DRM core to create `/dev/dri/renderD128`.
Since the driver already uses `DRM_GEM_SHMEM_DRIVER_OPS` (which supports
`prime_handle_to_fd`/`prime_fd_to_handle` for DMA-BUF), GBM and EGL work
immediately.

### Build & install as out-of-tree module

```bash
# Extract source from linux-source package
tar xf /usr/src/linux-source-7.0.tar.xz --wildcards '*/drivers/gpu/drm/hyperv/*'
cp -r linux-source-7.0/drivers/gpu/drm/hyperv/ /tmp/hyperv-build/

# Apply patch
cd /tmp/hyperv-build
sed -i 's/DRIVER_MODESET | DRIVER_GEM | DRIVER_ATOMIC/DRIVER_MODESET | DRIVER_GEM | DRIVER_ATOMIC | DRIVER_RENDER/' hyperv_drm_drv.c

# Create Makefile
cat > Makefile << 'EOF'
obj-m := hyperv_drm.o
hyperv_drm-y := hyperv_drm_drv.o hyperv_drm_modeset.o hyperv_drm_proto.o
KDIR := /lib/modules/$(shell uname -r)/build
all:
	make -C $(KDIR) M=$(CURDIR) modules
EOF

# Build
make

# Install (replaces in-tree module)
mkdir -p /lib/modules/$(uname -r)/extra
cp hyperv_drm.ko /lib/modules/$(uname -r)/extra/
# Remove the in-tree compressed module so depmod prefers ours
rm /lib/modules/$(uname -r)/kernel/drivers/gpu/drm/hyperv/hyperv_drm.ko.xz
depmod -a
update-initramfs -u -k $(uname -r)
```

### Remaining issue: drmGetDeviceFromDevId() — FIXED by libdrm patch

Even with `renderD128` created, KWin's `drmGetDeviceFromDevId()` still fails
because `hyperv_drm` is a **VMBus** device, not a PCI device. The DRM core's
`drmGetDeviceFromDevId()` maps from a KMS device to its render node using
PCI bus topology, which doesn't exist for VMBus devices.

### Patch 2: libdrm — VMBus bus type support

File: `xf86drm.c` in libdrm 2.4.124

Three changes:
1. Add `#define DRM_BUS_VMBUS 0x20` (after `DRM_BUS_VIRTIO`)
2. Add `{ "/vmbus", DRM_BUS_VMBUS }` to `get_subsystem_type()` bus_types array
3. Add `case DRM_BUS_VMBUS: return drmProcessPlatformDevice(...)` to `drmProcessDevice()`

After this patch, `drmGetDevice()` returns success on hyperv_drm. KWin no
longer logs `drmGetDeviceFromDevId() failed`.

### drmModeAddFB2 — RESOLVED (not a kernel bug)

After both patches, KWin can:
- ✅ Find the render node via `drmGetDevice()`
- ✅ Initialize OpenGL compositing (`gl2`)
- ✅ Advertise `linux-dmabuf` to Wayland clients
- ✅ Lamco detects `DMA-BUF zero-copy: Guaranteed`

Earlier notes reported `drmModeAddFB2()` returning `EINVAL` with the kernel
log `drm_internal_framebuffer_create: no buffer object handle for plane 0`,
and attributed it to a "dumb buffer pitch mismatch" (pitch 3072 vs 4096).
**That diagnosis was wrong.** Verified on the running VM (2026-08-18):

- `drmModeAddFB2` **succeeds** on hyperv_drm. A 640×480 XRGB8888 dumb buffer
  returns a valid `fb_id`, can be `mmap`ed via `MAP_DUMB`, and written.
- The earlier "pitch=3072 for w=1024" was a **test struct-packing bug**:
  `struct drm_mode_create_dumb` has `height` as the first field, not
  `width`. With correct packing (`struct.pack("=IIIIIIq", height, width,
  32, 0,0,0,0)`) the pitch is exactly `width * 4` as expected.
- The `no buffer object handle for plane 0` error was also a **test
  struct-packing bug**: `struct drm_mode_fb_cmd2` has a `flags` field
  between `pixel_format` and `handles[4]`. Omitting it shifts `handles`
  to where `flags` lives, so `drm_gem_object_lookup(file, 0)` fails:
  pack as `struct.pack("=IIIIIIq", height, width, 32, 0,0,0,0)`.

hyperv_drm's dumb_create (via `DRM_GEM_SHMEM_DRIVER_OPS` →
`drm_mode_size_dumb`) computes pitch = `width * cpp` correctly. **No
dumb buffer pitch patch and no `drm_gem_shmem_dumb_create` patch are
needed.** The shmem GEM objects can be wrapped into KMS framebuffers and
blitted to VRAM by the existing `hyperv_blit_to_vram_rect` path.

### Remaining issue: DMA-BUF import for scanout

What KWin's ScreenCast actually needs is to wrap a **DMA-BUF** (allocated
on the render node via GBM) into a KMS framebuffer. `drmModeAddFB2` on a
DMA-BUF GEM object imported into card0 may still differ from the dumb
buffer case, since the imported object's backing is the render node's
shmem, not a dumb buffer allocated on card0. This is the next thing to
verify end-to-end with KWin + Lamco now that the basic `AddFB2` path is
confirmed working.

## Software Rendering Configuration

### Plasma env file for KWin

File: `/etc/xdg/plasma-workspace/env/kwin-software-render.sh`
```bash
#!/bin/bash
# Force Mesa software rasterizer for KWin on Hyper-V.
# The kms_swrast override is REQUIRED: without it Mesa's kmsro loader fails
# with "driver missing" on the VMBus device, and KWin logs
# "Failed to open drm node" / "kwin_wayland_drm: Failed to create framebuffer:
# Invalid argument" in a loop. With the override, KWin successfully does
# PRIME_FD_TO_HANDLE → MODE_ADDFB2 → MODE_ATOMIC page flips.
export LIBGL_ALWAYS_SOFTWARE=1
export MESA_LOADER_DRIVER_OVERRIDE=kms_swrast
```

### Why MESA_LOADER_DRIVER_OVERRIDE is now required

Earlier notes said the override "breaks EGL on GBM". That was recorded **before
the hyperv_drm DRIVER_RENDER patch and the libdrm VMBus patch existed**. With
both patches in place (so `/dev/dri/renderD128` exists and `drmGetDevice2()`
returns the render node), verified on the running VM (2026-08-18):

| Setting | EGL on GBM (renderD128) | KWin compositing | KWin framebuffer creation |
|---------|------------------------|------------------|---------------------------|
| `LIBGL_ALWAYS_SOFTWARE=1` only | ✅ eglInitialize 1.5 | ❌ `kmsro: driver missing` → `Failed to open drm node: ""` | ❌ EINVAL loop |
| `+MESA_LOADER_DRIVER_OVERRIDE=kms_swrast` | ✅ eglInitialize 1.5 | ✅ DRM backend, atomic commits | ✅ `MODE_ADDFB2` + `MODE_ATOMIC` succeed, 0 errors |
| `+MESA_LOADER_DRIVER_OVERRIDE=swrast` | ✅ eglInitialize 1.5 | (not tested end-to-end) | — |

The override makes Mesa's kmsro loader select the `kms_swrast` driver for the
VMBus device instead of failing to identify it (`pci id ... driver (null)`).
A full graphical-session restart is required for the env change to take effect
(restarting only kwin does not re-read plasma env files).

## Lamco RDP Server Configuration

### systemd user service

The service must use `--dbus-service` mode (not `--config`) for the
`--grant-permission` flow to work:

```ini
[Service]
Type=dbus
BusName=io.lamco.RdpServer
ExecStart=/usr/bin/lamco-rdp-server --dbus-service
```

With `--dbus-service`, Lamco registers `io.lamco.RdpServer` on D-Bus and
starts the TCP listener on port 3389 after the Portal permission is granted.

### TLS configuration

```toml
[security]
security_mode = "tls"
auth_method = "none"
require_tls_13 = false
# No credssp_credentials section when auth_method = "none"
```

### One-time permission grant

Lamco requires a one-time interactive Portal permission grant:
```bash
lamco-rdp-server --grant-permission
```
This opens an xdg-desktop-portal dialog. The user must click "Allow".
The restore token is stored in GNOME Keyring for future sessions.

**Gotcha**: If GNOME Keyring is locked (e.g., after switching from GNOME to KDE),
the restore token becomes inaccessible. Delete the keyring and re-grant:
```bash
rm -f ~/.local/share/keyrings/login.keyring
gnome-keyring-daemon --start --components=secrets
lamco-rdp-server --grant-permission
```

### WAYLAND_DISPLAY in systemd user environment

LightDM does not automatically import `WAYLAND_DISPLAY` into the systemd
user environment. The Lamco service has `ConditionEnvironment=WAYLAND_DISPLAY`
which fails. Fix by either:
1. Removing `ConditionEnvironment` from the service file, or
2. Importing it in a Plasma env script:
   ```bash
   # /etc/xdg/plasma-workspace/env/99-import-wayland.sh
   sleep 3
   systemctl --user set-environment WAYLAND_DISPLAY=wayland-0
   ```

## Resolution Fix

hyperv_drm reports 1024x768 as the preferred mode. Override with `monitors.xml`:

```xml
<!-- ~/.config/monitors.xml (for GNOME/mutter) -->
<monitors>
  <configuration>
    <layoutmode>logical</layoutmode>
    <logicalmonitor>
      <x>0</x><y>0</y><scale>1</scale><primary>yes</primary>
      <monitor>
        <monitorspec>
          <connector>Virtual-1</connector>
          <vendor>unknown</vendor>
          <product>unknown</product>
          <serial>unknown</serial>
        </monitorspec>
        <mode>
          <width>1920</width>
          <height>1080</height>
          <rate>60.000</rate>
        </mode>
      </monitor>
    </logicalmonitor>
  </configuration>
</monitors>
```

The `video=Virtual-1:1920x1080@60` kernel cmdline parameter does NOT
override mutter's mode selection.

## vgem Module (Virtual GPU Render Node)

The `vgem` kernel module creates a virtual `/dev/dri/renderD128`:
```bash
modprobe vgem
echo "vgem" > /etc/modules-load.d/vgem.conf
echo 'KERNEL=="renderD128", GROUP="video", MODE="0660"' > /etc/udev/rules.d/99-vgem-render.rules
```

### vgem limitations

- GBM device creation succeeds on vgem's renderD128
- EGL on GBM with `LIBGL_ALWAYS_SOFTWARE=1` succeeds
- GBM buffer allocation (1920x1080 BGRA) succeeds
- BUT: vgem cannot create KMS framebuffers (`Failed to create framebuffer: Invalid argument`)
- BUT: KWin's `drmGetDeviceFromDevId()` still fails (VMBus vs PCI topology)
- vgem's renderD128 is on a separate device (card1), not on hyperv_drm (card0)

**Conclusion**: vgem alone does not fix KWin ScreenCast on Hyper-V.

## Network & SSH Persistence on Parrot OS

Parrot's default install has SSH disabled and NetworkManager not auto-connecting:
```bash
sudo systemctl enable ssh
sudo nmcli connection modify eth0 connection.autoconnect yes
sudo nmcli device set eth0 managed yes
```

## NVIDIA Driver Cleanup

Parrot ships with NVIDIA drivers that interfere with Mesa:
```bash
sudo apt-get purge -y libegl-nvidia0 libgl1-nvidia-glvnd-glx libgles-nvidia1 libgles-nvidia2
sudo apt-get purge -y nvidia-driver nvidia-kernel-dkms
```

NVIDIA's EGL libraries hijack driver selection, preventing Mesa swrast from
being used. Must purge them before software rendering works.

## VMCreate Integration

### InstallLamcoRdpStep.cs changes

1. Changed systemd unit from `Type=dbus` + `--dbus-service` (correct for grant flow)
2. Added monitors.xml creation for 1920x1080 resolution
3. Added vgem module loading (best-effort for KDE)
4. Added `LIBGL_ALWAYS_SOFTWARE=1` env for software rendering

### EnableGraphicalAutologinStep.cs changes

1. Added monitors.xml creation
2. Added vgem module loading
3. Handles GDM (GNOME), SDDM (KDE), and LightDM (Parrot) display managers

### Future: DKMS package for patched hyperv_drm

The one-line `DRIVER_RENDER` patch should be packaged as a DKMS module that:
1. Builds against the guest's kernel headers
2. Replaces the in-tree `hyperv_drm.ko` at install time
3. Updates initramfs
4. Survives kernel upgrades (DKMS auto-rebuilds)

This is necessary (but not sufficient) for KWin ScreenCast on Hyper-V.
Additional work is needed to fix `drmGetDeviceFromDevId()` for VMBus devices
or to patch KWin to fall back to opening renderD128 directly.

## Zero-Copy DMA-BUF ScreenCast Investigation

### Summary of findings (2026-08-18)

The SHM fallback (`KWIN_SCREENCAST_FORCE_SHM=1`) works at ~17-21 FPS.
Zero-copy DMA-BUF ScreenCast was investigated extensively but hits a
PipeWire buffer negotiation failure. The DMA-BUF allocation and EGL import
pipeline **does work** in isolation — the blocker is in the PipeWire link
negotiation between KWin (producer) and lamco (consumer).

### What works (verified with standalone tests)

1. **GBM allocation on card0**: `gbm_bo_create_with_modifiers` on `/dev/dri/card0`
   succeeds, producing a DMA-BUF fd with stride=4096, modifier=INVALID
2. **EGL DMA-BUF import**: `eglCreateImageKHR(EGL_LINUX_DMA_BUF_EXT)` succeeds
   with `EGL_EXT_image_dma_buf_import` on kms_swrast
3. **GL texture + FBO**: `glEGLImageTargetTexture2DOES` + `glFramebufferTexture2D`
   produces a complete FBO

### What does NOT work

1. **GBM allocation on renderD128**: `DRM_IOCTL_MODE_CREATE_DUMB` returns `EACCES`
   on the render node. hyperv_drm's render node (created by the DRIVER_RENDER patch)
   does not support dumb buffer allocation — only the primary node (card0) does.
2. **PipeWire DmaBuf buffer negotiation**: KWin allocates DmaBuf buffers via GBM
   on card0, but the PipeWire link to lamco fails with `port_use_buffers(1:0:-1)
   error: Input/output error` → `Buffer allocation failed`
3. **SyncTimeline buffer params**: KWin's first buffer param requires
   `SPA_META_SyncTimeline` with `planeCount + 2` blocks. lamco doesn't support
   explicit sync, causing a second negotiation round that fails with `-EIO`
4. **Format renegotiation**: After KWin receives modifiers from the client and
   calls `pw_stream_update_params`, the re-negotiation triggers a new link
   that fails because lamco's stream returns `-EIO` during the renegotiation

### Test tools

Standalone test tools used during diagnosis (historically kept under
`patches/hyperv-drm-render-node/`, removed since):

- **`dmabuf_egl_test.c`**: Tests the full DMA-BUF → EGL import → GL texture →
  FBO pipeline. Must run within the Wayland session:
  ```bash
  gcc -o dmabuf_egl_test dmabuf_egl_test.c -lgbm -lEGL -lGLESv2 \
      -lwayland-client -I/usr/include/libdrm -Wall
  sudo -u user WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/1000 \
      LIBGL_ALWAYS_SOFTWARE=1 MESA_LOADER_DRIVER_OVERRIDE=kms_swrast \
      ./dmabuf_egl_test
  ```

- **`pw_consumer_test.c`**: Standalone PipeWire consumer that connects to a
  given node and logs every step of negotiation (format, buffers, state):
  ```bash
  gcc -o pw_consumer_test pw_consumer_test.c \
      $(pkg-config --cflags --libs libpipewire-0.3 libspa-0.2) \
      -I/usr/include/libdrm -Wall -g
  ./pw_consumer_test <node_id> [dmabuf=1|0]
  ```

### Root cause analysis (UPDATED 2026-08-18)

**The DmaBuf pipeline works with a standalone PipeWire consumer!** Using
`pw_consumer_test` (our test tool), we connected directly to KWin's ScreenCast
node via the shared PipeWire daemon and received DmaBuf buffers successfully:

```
=== PW Consumer Test: node_id=61 dmabuf=1 ===
[PARAM_CHANGED] Negotiated: 1024x768 format=8 modifier=0xffffffffffffff
[ADD_BUFFER] data[0]: type=DmaBuf(3) fd=22 maxsize=3145728
[ADD_BUFFER] data[0]: type=DmaBuf(3) fd=23 maxsize=3145728
[ADD_BUFFER] data[0]: type=DmaBuf(3) fd=24 maxsize=3145728
[STATE] paused -> streaming
[OK] Stream is now STREAMING!
```

This works with both `MAP_BUFFERS` only and `MAP_BUFFERS|DRIVER|RT_PROCESS`
(matching lamco's flags). The problem is **not** in KWin, PipeWire, or the
flags — it's in lamco's **private Portal FD** connection.

### The real blocker: lamco's private Portal FD

lamco connects to PipeWire via a **private file descriptor** obtained from
`xdg-desktop-portal`'s `OpenPipeWireRemote()` method. This is a private
PipeWire connection where node IDs are only valid on that FD.

Our `pw_consumer_test` connects to the **shared PipeWire daemon** (via
`pw_context_connect()`), where WirePlumber can auto-link the streams.

The difference:
- **Shared daemon connection** (our test): WirePlumber handles link creation
  → KWin allocates DmaBuf buffers → consumer receives them → STREAMING ✅
- **Private Portal FD** (lamco): lamco connects with `AUTOCONNECT` flag,
  but the private FD may not support the same link negotiation path →
  buffer allocation fails with `-EIO`

### Next step

The next step is to make `pw_consumer_test` connect via a **Portal FD** (like
lamco does) instead of the shared daemon. This will tell us if the private FD
is the root cause. The test would need to:
1. Call `OpenPipeWireRemote()` via D-Bus to get the private FD
2. Connect `pw_context_connect_fd()` on that FD
3. Connect the stream to the portal-provided node ID
4. Check if DmaBuf buffers are received

If the private FD test fails, the fix is in how lamco handles the private
connection. If it succeeds, the problem is elsewhere in lamco's code.

**Portal FD test status**: The `Start` portal method does not return a Response
signal when called from our test harness. The `xdg-desktop-portal-kde` aborts
the screencast immediately. lamco avoids this by using the `libei` session
strategy which goes through a different code path.

**Shared daemon patch result**: We patched lamco's `src/server/mod.rs` to replace
the private Portal FD with `connect_to_pipewire_daemon()` (the shared daemon).
The patch was built and tested. The log confirmed: `Portal FD: 12 — switching
to shared PipeWire daemon` and `Connected to shared PipeWire daemon, FD: 11`.

However, buffer allocation still failed: `Buffer allocation failed`. The reason:
the portal creates a **private PipeWire connection** where node IDs are only
valid on that FD. When lamco connects to the shared daemon, the portal-provided
node_id is not visible — WirePlumber can't link the streams.

The `pw_consumer_test` worked because both KWin and the test consumer were on
the shared daemon, where KWin's node was published globally. The portal's
private FD creates an isolated connection where node IDs are local.

**Conclusion**: Switching from private Portal FD to shared daemon doesn't work
because the node_id from the portal is not valid on the shared daemon. The fix
needs to either:
1. Make KWin publish its ScreenCast node on the shared daemon (not just the
   private portal connection)
2. Fix the buffer allocation on the private Portal FD connection
3. Use a different approach entirely (e.g., KWin plugin that captures directly
   to SHM without PipeWire)

### KWin patches applied (in `/tmp/kwin-6.3.6/` on VM)

1. **SHM fallback in `onStreamAddBuffer`**: When `DmaBufScreenCastBuffer::create`
   fails, falls back to `MemFdScreenCastBuffer::create`
2. **EGL import test in `testCreateDmaBuf`**: Tests `importDmaBufAsTexture` before
   advertising DMA-BUF support
3. **Modifier negotiation fix**: Treats `DRM_FORMAT_MOD_INVALID` as compatible
   with linear modifier
4. **SyncTimeline param removal**: Removed the SyncTimeline buffer param to
   avoid the second negotiation round
5. **Skip renegotiation**: When `m_dmabufParams` already matches, skip
   `pw_stream_update_params` to avoid triggering a new link
6. **Debug logging in `DmaBufScreenCastBuffer::create`**: Added qCWarning
   messages at each failure point

### PipeWire patches applied (reverted)

- **Force DmaBuf data type** in `stream.c`: Forced DmaBuf when offered in
  the dataType bitmask. This was **reverted** because it removed the MemFd
  fallback option, making buffer allocation fail completely.

### Current working configuration

The SHM fallback is the only working configuration:
```bash
# /etc/xdg/plasma-workspace/env/kwin-software-render.sh
#!/bin/bash
export LIBGL_ALWAYS_SOFTWARE=1
export MESA_LOADER_DRIVER_OVERRIDE=kms_swrast
export KWIN_SCREENCAST_FORCE_SHM=1
```

This produces ~17-21 FPS via MemFd shared memory buffers.

## Performance & UX Hardening (2026-08-21/22)

The SHM capture above is the base layer; the end-to-end Enhanced Session
pipeline was then brought to production quality. Full narrative in
`KDE-SCREENCAST-STACK.md` ("Findings and Solutions 2026-08-21/22");
quick reference:

| Area | Root cause | Fix |
|---|---|---|
| Encode 48→5 ms | CRF 1 mapping; wasteful BGRA→I420; PipeWire 3-buffer starvation | CRF clamp 15-30; `bgra_to_i420` integer converter; buffer_count 5 |
| Persistent artifacts | compositor damage hints under-report (≤25.8 pp); skipped frames' hints discarded | pixel-diff primary (~1.9 ms) + damage accumulation |
| Grey blacks | mstsc doesn't expand limited-range Y16 | full-range BT.601 + VUI behind `egfx.color_range` |
| Two cursors | llvmpipe KWin bakes cursor into video (no portal mode removes it) | transparent guest XCursor theme + ColorPointer PDU |
| Cursor "glitch" | alpha>128 dropped 85 anti-aliased pixels | a>0 draws; only a==0 punches through |
| Session drop at connect | `Vec::with_capacity` → 0-byte WriteCursor → encode error killed client loop | allocate-encode-truncate (ironrdp fork `ServerEvent::Pointer`) |

Operational gotchas (all reproducible):

- **Portal dialog dance**: blind input injection (ydotool) is unreliable;
  the reliable sequence is unlock sessions → center-screen click → Enter,
  or a manual "Share" click on the Hyper-V console. Restore tokens die
  with SIGKILL (use `sudo killall -x`).
- **Log parsing**: tracing embeds ANSI codes between key and value —
  strip `\x1b\[[0-9;]*m` before regexing (helper scripts under
  `TestResults/`). VM clock is UTC; local `date` cutoffs mis-slice logs.
- **Cargo test caches** in git checkouts run stale binaries ("Finished in
  0.26s" + 0 tests = cached); `touch` the source and check the checkout
  glob actually matches the pinned rev.
- **IronRDP fork pin**: `lamco-rdp-server/Cargo.toml` pins all 23 ironrdp
  crates by full SHA. Bump with a literal string replace + count check —
  a scripted replace once blanked every rev to `""` (caught by the count).
