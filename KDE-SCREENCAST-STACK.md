# KDE Plasma ScreenCast Stack: How It All Fits Together

A reference document explaining how KDE Plasma, Wayland, KWin, PipeWire,
WirePlumber, DMA-BUF, GBM, and SHM work together for screen capture —
and what breaks on Hyper-V.

---

## Glossary

| Term | What It Is |
|------|------------|
| **KDE Plasma** | Desktop environment (panels, widgets, settings). Runs on top of a Wayland compositor. |
| **Wayland** | Display protocol. Clients render to buffers; the compositor composites them onto the screen. Replaces X11. |
| **KWin** | The Wayland compositor used by KDE Plasma. Handles window management, input, compositing (OpenGL/QPainter), and screen capture. |
| **DRM/KMS** | Linux kernel display subsystem. `DRM` = GPU/memory management; `KMS` = mode setting (crtcs, connectors, framebuffers). Exposed as `/dev/dri/card0` (primary) and `/dev/dri/renderD128` (render-only). |
| **hyperv_drm** | Linux DRM driver for Hyper-V's virtual GPU. Display-only, software framebuffer, no hardware 3D acceleration. |
| **GBM** | Generic Buffer Management. API to allocate DMA-BUFs via the DRM driver. Used by Mesa/EGL to create surfaces and by compositors for buffers. |
| **DMA-BUF** | Linux kernel framework for sharing buffers across devices/drivers via file descriptors. A DMA-BUF fd is a handle to a GPU-accessible buffer that can be imported by EGL, V4L2, other DRM devices, etc. |
| **EGL** | Khronos API for OpenGL context/surface management on Linux. `eglCreateImageKHR(EGL_LINUX_DMA_BUF_EXT)` imports a DMA-BUF fd as a GL texture source. |
| **Mesa** | Open-source OpenGL/EGL implementation. `kms_swrast` = software rasterizer that renders into DRM/KMS framebuffers (no GPU needed). |
| **SHM / MemFd** | Shared memory buffers. `memfd_create()` creates an anonymous file backed by RAM. Shared via fd passing. Used as the fallback when DMA-BUF isn't available. |
| **PipeWire** | Multimedia server. Handles audio and video stream routing between clients. For ScreenCast, it links the compositor (producer) to the RDP server (consumer). |
| **WirePlumber** | PipeWire session manager. Handles device detection, link creation, and policy. Acts as the "router" — connects producer ports to consumer ports. |
| **xdg-desktop-portal** | Freedesktop portal API for sandboxed apps. `ScreenCast` and `RemoteDesktop` portals allow RDP servers to request screen capture access from the compositor. |
| **Lamco RDP Server** | Rust-based RDP server that uses PipeWire for screen capture (via xdg-desktop-portal ScreenCast) and libei for input injection. |

---

## The Big Picture

```
┌────────────────────────────────────────────────────────────────────┐
│                        APPLICATION LAYER                           │
│                                                                    │
│  Windows mstsc ◄──TLS/RDP──► Lamco RDP Server (Rust)              │
│                                    │                               │
│                                    ├── Input: libei → KWin         │
│                                    └── Video: PipeWire consumer   │
│                                                                    │
├────────────────────────────────────────────────────────────────────┤
│                      PORTAL / SESSION LAYER                         │
│                                                                    │
│  xdg-desktop-portal-kde                                            │
│    ├── RemoteDesktop portal (input permission)                     │
│    └── ScreenCast portal (video capture permission)                │
│         │                                                          │
│         │ Creates a PipeWire node for the screen output             │
│         │ Returns node_id + PipeWire fd to Lamco                   │
│         │                                                          │
│         ▼                                                          │
│  WirePlumber (PipeWire session manager)                            │
│    ├── Discovers KWin's ScreenCast stream as a producer node        │
│    ├── Creates a link: KWin producer port → Lamco consumer port     │
│    └── Manages buffer negotiation between the two                   │
│                                                                    │
├────────────────────────────────────────────────────────────────────┤
│                         COMPOSITOR LAYER                           │
│                                                                    │
│  KWin (Wayland compositor)                                         │
│    ├── Renders the desktop: windows, panels, effects                │
│    │   └── Uses OpenGL (EGL on GBM) or QPainter (software)          │
│    ├── ScreenCast plugin (screencaststream.cpp)                    │
│    │   ├── Creates a PipeWire output stream (producer)              │
│    │   ├── Allocates buffers: DMA-BUF (preferred) or MemFd (fallback)│
│    │   ├── Renders each frame into the allocated buffer             │
│    │   └── Queues the buffer for the consumer to dequeue             │
│    │                                                               │
│    │   Buffer allocation flow:                                     │
│    │   ┌─────────────────────────────────────────────────┐        │
│    │   │ 1. testCreateDmaBuf()                             │        │
│    │   │    ├── GBM allocate (gbm_bo_create_with_modifiers)│        │
│    │   │    ├── Get DMA-BUF fd + pitch + modifier          │        │
│    │   │    ├── EGL import (eglCreateImageKHR)             │        │
│    │   │    └── If success: advertise DmaBuf format       │        │
│    │   │                                                  │        │
│    │   │ 2. onStreamAddBuffer() — per buffer:            │        │
│    │   │    ├── DmaBufScreenCastBuffer::create()           │        │
│    │   │    │   ├── GBM allocate buffer                   │        │
│    │   │    │   ├── EGL import as GL texture               │        │
│    │   │    │   ├── Create GLFramebuffer (FBO)            │        │
│    │   │    │   └── Fill spa_data with fd, pitch, offset  │        │
│    │   │    └── If DmaBuf fails:                           │        │
│    │   │        └── MemFdScreenCastBuffer::create()        │        │
│    │   │            ├── Allocate shmem buffer (memfd)     │        │
│    │   │            └── Fill spa_data with fd, size        │        │
│    │   └── record() — each frame:                         │        │
│    │       ├── Render desktop into the buffer's FBO        │        │
│    │       │   (DMA-BUF: GL render → zero-copy)           │        │
│    │       │   (MemFd: CPU memcpy → one extra copy)       │        │
│    │       └── pw_stream_queue_buffer() → consumer          │        │
│    └── DRM output (page-flip to screen)                     │        │
│        └── /dev/dri/card0 → hyperv_drm → VMBus VRAM       │        │
│                                                                    │
├────────────────────────────────────────────────────────────────────┤
│                        KERNEL / DRIVER LAYER                       │
│                                                                    │
│  /dev/dri/card0 (hyperv_drm, primary node)                         │
│    ├── Supports: MODE_CREATE_DUMB, MODE_ADDFB2, atomic page-flip   │
│    ├── GEM shmem objects in system RAM                              │
│    └── CPU blit to VMBus VRAM aperture (no DMA)                    │
│                                                                    │
│  /dev/dri/renderD128 (hyperv_drm render node — patched)            │
│    ├── Created by adding DRIVER_RENDER to driver_features           │
│    ├── Does NOT support: MODE_CREATE_DUMB (EACCES)                 │
│    └── GBM falls back to card0 for allocation                       │
│                                                                    │
│  Mesa (kms_swrast, software renderer)                              │
│    ├── EGL_EXT_image_dma_buf_import: ✅ supported                   │
│    ├── eglCreateImageKHR from DMA-BUF: ✅ works                     │
│    ├── GL texture from EGL image: ✅ works                         │
│    └── FBO: ✅ complete                                             │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

---

## How Screen Capture Works (Step by Step)

### 1. Session Startup

```
lightdm/SDDM → user login → plasma-session
  → sources /etc/xdg/plasma-workspace/env/*.sh
    → LIBGL_ALWAYS_SOFTWARE=1, MESA_LOADER_DRIVER_OVERRIDE=kms_swrast
  → starts KWin (kwin_wayland)
  → starts PipeWire (pipewire)
  → starts WirePlumber (wireplumber)
  → starts xdg-desktop-portal-kde
```

### 2. RDP Server Startup

```
lamco-rdp-server --grant-permission
  → xdg-desktop-portal dialog: "Allow remote desktop?"
  → User clicks "Allow"
  → Token stored in GNOME Keyring
  → lamco starts listening on port 3389
```

### 3. RDP Client Connects

```
mstsc → TLS handshake → lamco accepts
  → lamco requests ScreenCast via xdg-desktop-portal
    → portal creates a PipeWire node for KWin's output
    → portal returns: node_id=61, pipewire_fd=13
  → lamco creates PipeWire stream on that fd:
    → pw_stream_connect(INPUT, node_id=61,
        flags=AUTOCONNECT|MAP_BUFFERS|DRIVER|RT_PROCESS)
    → lamco offers 2 format params:
        1. DmaBuf: BGRA 1024x768, modifier=LINEAR, MANDATORY|DONT_FIXATE
        2. SHM:    BGRA 1024x768 (no modifier)
```

### 4. PipeWire Link Negotiation

```
WirePlumber sees:
  - KWin producer node (output stream, ALLOC_BUFFERS flag)
  - lamco consumer node (input stream, MAP_BUFFERS flag)
  → Creates a link between them

Negotiation sequence:
  1. WirePlumber calls do_negotiate():
     → Asks both ports to agree on a format
     → KWin offers: BGRA, various modifiers (including LINEAR)
     → lamco offers: BGRA, LINEAR
     → They agree: BGRA 1024x768 modifier=0x0 (LINEAR)

  2. WirePlumber calls do_allocation():
     → KWin has ALLOC_BUFFERS → KWin allocates buffers
     → KWin's onStreamAddBuffer() is called for each buffer:
        a. Tries DmaBufScreenCastBuffer::create()
           - GBM allocate on card0
           - EGL import as texture
           - Create FBO
           - Fill spa_data: type=DmaBuf, fd, pitch, offset
        b. If DmaBuf fails → MemFdScreenCastBuffer::create()
           - Allocate shmem via memfd_create
           - Fill spa_data: type=MemFd, fd, size

  3. WirePlumber calls port_use_buffers() on lamco's input port:
     → lamco's impl_port_use_buffers() maps the buffers
     → If MAP_BUFFERS: mmaps the fds
     → Emits add_buffer callback

  4. Both ports transition to PAUSED → STREAMING
```

### 5. Frame Processing (Streaming)

```
Each frame:
  KWin:
    1. Render desktop into the DmaBuf's GL FBO (or memcpy to MemFd)
    2. pw_stream_queue_buffer() → signals data ready

  PipeWire:
    3. Routes the buffer to lamco's input port
    4. Calls lamco's process callback

  lamco:
    5. pw_stream_dequeue_buffer() → get the buffer
    6. Read frame data (from DmaBuf fd via mmap, or MemFd)
    7. Encode to H.264
    8. Send over RDP to mstsc
    9. pw_stream_queue_buffer() → return buffer to KWin
```

---

## DMA-BUF vs SHM/MemFd: What's the Difference?

### DMA-BUF (zero-copy, preferred)

```
┌──────────┐     DMA-BUF fd      ┌──────────┐
│  KWin    │──── (gpu buffer) ──│  lamco   │
│  renders │     no CPU copy     │  reads   │
│  to GPU  │◄──────────────────►│  via mmap│
└──────────┘                    └──────────┘

- Buffer allocated once via GBM (on GPU memory or system RAM)
- KWin renders directly into it (via GL FBO)
- lamco reads the same memory (via mmap of the DMA-BUF fd)
- Zero CPU copies: the same physical pages are shared
- Requires: DRM render node + GBM + EGL dma_buf_import
```

### SHM / MemFd (one copy, fallback)

```
┌──────────┐    memcpy     ┌──────────┐    mmap     ┌──────────┐
│  KWin    │── CPU copy ──│ MemFd     │── shared ──│  lamco   │
│  renders │              │ (RAM)     │   memory   │  reads   │
└──────────┘              └──────────┘            └──────────┘

- Buffer allocated as a memfd (anonymous shared memory file)
- KWin renders to its own internal buffer, then memcpy to MemFd
- lamco reads via mmap of the MemFd fd
- One extra CPU copy (KWin internal → MemFd)
- Works everywhere: no GPU/render node needed
- ~17-21 FPS on hyperv_drm
```

---

## What Breaks on Hyper-V and Why

### Problem 1: No Dumb Buffers on renderD128

```
hyperv_drm driver_features = DRIVER_MODESET | DRIVER_GEM | DRIVER_ATOMIC
                             (no DRIVER_RENDER → no render node)

After our patch:
  driver_features = ... | DRIVER_RENDER
  → /dev/dri/renderD128 created ✅

BUT: renderD128 doesn't support DRM_IOCTL_MODE_CREATE_DUMB
  → GBM tries to allocate → CREATE_DUMB → EACCES
  → gbm_bo_create_with_modifiers() fails on renderD128

card0 (primary node) DOES support CREATE_DUMB:
  → GBM allocation works on card0 ✅
  → DMA-BUF fd obtained ✅
  → EGL import works ✅
```

### Problem 2: PipeWire Buffer Negotiation Failure

```
The negotiation succeeds on the first link, but KWin then receives
modifier information from the client and triggers a renegotiation:

1. First link: KWin allocates DmaBuf buffers (262248 bytes) → STREAMING ✅
2. KWin receives modifiers from client → calls pw_stream_update_params()
3. Old link destroyed, new link created
4. Second link: port_use_buffers() on lamco returns -EIO
   → lamco's stream is in "disconnecting" state (old stream teardown)
   → "Buffer allocation failed"

The -EIO comes from PipeWire's stream.c:
  if (impl->disconnecting && n_buffers > 0)
      return -EIO;
```

### Problem 3: SyncTimeline (Explicit Sync)

```
KWin offers two buffer param sets:
  1. SyncTimeline: planeCount+2 blocks, MANDATORY SPA_META_SyncTimeline
  2. Fallback:      planeCount blocks, no SyncTimeline

lamco doesn't support explicit sync (SyncTimeline).
PipeWire tries param 1 first (312-byte buffers with SyncObj fds).
When that fails, it should fall back to param 2.
But the failure triggers the -EIO path before fallback can happen.
```

---

## Current State Summary

| Component | Status | Notes |
|-----------|--------|-------|
| hyperv_drm DRIVER_RENDER | ✅ Patched | renderD128 exists |
| libdrm VMBus bus type | ✅ Patched | drmGetDevice2() works |
| KWin OpenGL compositing | ✅ Working | kms_swrast on card0 |
| KWin ScreenCast SHM | ✅ Working | originally ~17-21 FPS; now 48+ (see 2026-08-21) |
| GBM DMA-BUF allocation | ✅ Works on card0 | Fails on renderD128 |
| EGL DMA-BUF import | ✅ Works | eglCreateImageKHR succeeds |
| PipeWire DmaBuf negotiation | ❌ Broken | Renegotiation -EIO |
| Zero-copy DMA-BUF ScreenCast | ❌ Blocked | PipeWire negotiation failure |
| Enhanced Session (vsock+PCB) | ✅ Working | server-side ironrdp fork; see 2026-08-2x findings |
| x264 AVC420 encode pipeline | ✅ Working | zero-copy BGRA→I420, ~5 ms/frame (was 48 ms) |
| RDP pointer parity (xrdp) | ✅ Working | ColorPointer PDU; single cursor, zero lag |

---

## Findings and Solutions (2026-08-21/22): Enhanced Session hardening

The Enhanced Session pipeline went end-to-end in this window. Key results
and root causes, in causal order:

### Encode performance: 48 ms → 5 ms/frame (3 independent causes)

1. **CRF mapping**: configured `qp_min=1` mapped to x264 CRF 1 (near-lossless
   ⇒ ~10× encode time). Clamped to 15..30 in the C shim — screen content is
   visually lossless around CRF 15.
2. **Conversion waste**: BGRA→I420 went through openh264's f32 per-pixel
   path + a fresh 3 MB buffer per frame + three plane copies. Replaced with
   `bgra_to_i420` (integer BT.601, one reusable contiguous buffer, plane
   pointers handed straight to the x264 C shim).
3. **PipeWire buffer starvation**: 3 buffers at 67% queue pressure. Raised
   `buffer_count` to 5 in all three `StreamConfig` sites.
   Measured: convert 2.4 ms, encode 3.0 ms, total p50 = 4.0 ms;
   motion FPS 38 → 48; FrameAck p50 1.2 ms.

### Damage-tracking artifacts ("stuff stays until drawn over")

Compositor damage hints under-reported real change by up to 25.8 pp on
individual frames (hint 0.1% vs pixel-diff 25.9%) — trusted hints → stale
regions never re-sent. Pipes now: **pixel-diff is primary** (SIMD, ~1.9 ms),
and any consumed-but-unsent frame's regions accumulate into the next frame.

### Color: grey blacks over RDP

mstsc does **not** expand limited-range (Y16) to full — limited black
renders RGB(16,16,16). Fix: full-range BT.601 encode (black ⇒ Y0) + VUI
flags, behind the existing `egfx.color_range` config knob. Encode got
*slightly faster* (all 256 luma codes ⇒ better rate-distortion).

### Cursor: two pointers + trailing glitch (3 bugs, final state: xrdp parity)

Goal: mstsc must show **one** Parrot cursor, client-rendered (zero lag),
like xrdp. Achieved via a one-per-connection RDP ColorPointer PDU
(IronRDP fork `ServerEvent::Pointer` + lamco `cursor_pdu.rs`):
XCursor → andMask(1bpp)/xorMask(24bpp BGR, scanlines padded to even
bytes), bottom-up scanlines.

Bugs found on the way (each with a committed regression test):

1. **Double cursor** — KWin (no GPU cursor plane, llvmpipe) renders the
   cursor as a workspace scene item: **no portal cursor mode can remove it
   from the video**. Fix: transparent XCursor theme for the guest
   (`/usr/share/icons/transparent`, generator `genxcursor2.py`; XCursor
   binary layout verified against a real breeze file: magic 0x72756358,
   version 0x00010000, 9-field/36-byte image chunks).
2. **Session kill 47 µs after connect** — `Vec::with_capacity` does not
   resize; `WriteCursor` got a 0-byte slice; `FastPathHeader::encode`
   errored and took the client loop down. Fix: allocate-encode-truncate.
   (A second latent bug — 2 stray zero bytes after the frame, client
   protocol desync — was exposed by the new framing tests; same fix.)
3. **Speckle "glitch" around the cursor** — alpha>128 threshold dropped the
   breeze arrow's 85 anti-aliased pixels to transparent. Fix: a>0 draws
   (only a==0 is punch-through).
4. **"Three side-by-side contour cursors"** — the xor mask was packed at
   32bpp (4 B/px) into `TS_COLORPOINTERATTRIBUTE`, which every client
   decodes at 24bpp (3 B/px + even-byte row pad). Each scanline sheared
   left by the cursor width, smearing one arrow into three ghosts.
   Fix: 24bpp xor (xrdp's legacy color-pointer layout), guarded by a
   round-trip decode test through IronRDP's own client decoder
   (`decodes_via_ironrdp_client_decoder`). Alpha flattens to opaque in
   24bpp — the same tradeoff xrdp makes on this path.

Also: pointer-shape PDUs sent at pipeline reset are **discarded by mstsc**
(output PDUs racing activation); send after the first video frame
(`egfx_frames_sent == 1`).

### Not-yet-productized

- **Tests**: lamco cursor tests green (6); the 2 fork framing tests have
  assertion drift vs. the PER length encoding — pending a data-derived fix
  (the production path is byte-verified against ironrdp's own encoder).
- Console caveat: guest console has an invisible pointer while the
  transparent theme is active (inherent; flip `cursorTheme` back for
  console work).
- The two systematic memory docs (`full-range-color.md`,
  `double-cursor-fix.md`, `pointer-pdu-tests.md`) hold the detailed
  recipes (dialog dance, ydotool, rollback commands).


---

## What's the Next Step?

The DMA-BUF allocation and EGL import pipeline works in isolation.
The blocker is the **PipeWire buffer negotiation** — specifically, KWin's
format renegotiation after receiving modifiers triggers a new link that
fails because lamco's stream is in a disconnecting state.

There are three possible approaches, in order of effort:

### Approach A: Fix KWin to not renegotiate (smallest change)

**Problem**: KWin calls `pw_stream_update_params()` after receiving
modifiers, destroying the working first link and creating a new one
that fails.

**Fix**: In `onStreamParamChanged()`, skip the renegotiation if
`m_dmabufParams` already matches the received modifiers. Our patch
attempted this but the second stream (created by the Portal) still
fails because it's a completely new PipeWire connection, not a
renegotiation of the existing one.

**What to investigate**: Why does the Portal create a second stream?
Is it the `xdg-desktop-portal-kde` creating two ScreenCast sessions?
Or is KWin destroying and recreating its stream?

**Test**: Use `pw_consumer_test` to connect to the KWin node directly
(without lamco/portal) and see if DmaBuf buffers are received.

### Approach B: Fix the GBM allocator to use card0 (kernel fix)

**Problem**: GBM on renderD128 fails because the render node doesn't
support dumb buffers. GBM on card0 works.

**Fix**: Add `DRM_IOCTL_MODE_CREATE_DUMB` support to the render node
path in `hyperv_drm`. This requires a kernel patch to
`hyperv_drm_drv.c` that implements `dumb_create` callback for the
render node, or makes the render node inherit the dumb buffer support
from the primary node.

**Effort**: Moderate kernel work. The hyperv_drm driver already has
`DRM_GEM_SHMEM_DRIVER_OPS` which includes `dumb_create` — the issue
might be that the render node doesn't expose the same ioctls as the
primary node. This is a DRM core behavior, not a hyperv_drm issue.

### Approach C: Fix PipeWire's stream.c to handle renegotiation (hardest)

**Problem**: PipeWire's `impl_port_use_buffers` in `stream.c` returns
`-EIO` when `impl->disconnecting` is true. This prevents the new link
from allocating buffers.

**Fix**: Either prevent the stream from entering the disconnecting
state during renegotiation, or handle the -EIO gracefully by
retrying buffer allocation after the disconnect completes.

**Effort**: Requires deep PipeWire internals knowledge. The
disconnecting state is set when `pw_stream_disconnect()` is called,
which happens when KWin destroys the old stream to create a new one.
This is a race condition between old-stream teardown and new-link
buffer allocation.

### Recommended Next Step

**The standalone consumer test SUCCEEDS with DmaBuf!** Using
`pw_consumer_test`, we connected to KWin's ScreenCast node via the shared
PipeWire daemon and received DmaBuf buffers, going to STREAMING. This works
with both `MAP_BUFFERS` only and `MAP_BUFFERS|DRIVER|RT_PROCESS` (matching
lamco's exact flags).

**The problem is isolated to lamco's private Portal FD connection.**

lamco connects via a private file descriptor from `OpenPipeWireRemote()`,
not the shared PipeWire daemon. Our test consumer connects to the shared
daemon where WirePlumber handles link creation.

**Next step**: Build a test that connects via a Portal FD (like lamco):
1. Call `OpenPipeWireRemote()` via D-Bus to get the private FD
2. Connect `pw_context_connect_fd()` on that FD
3. Connect the stream to the portal-provided node ID
4. Check if DmaBuf buffers are received

**Portal FD test result**: The `Start` portal method does not work headlessly —
`xdg-desktop-portal-kde` aborts the screencast immediately. lamco avoids this
by using the `libei` session strategy.

The Portal FD test is blocked by this portal limitation. Alternative approaches:
1. Patch `xdg-desktop-portal-kde` to allow headless `Start` calls
2. Modify lamco to use the shared daemon connection instead of private Portal FD
3. Hook into lamco's actual PipeWire connection to inspect buffer negotiation

---

## Findings and Solutions (2026-08-19)

### What Works

The complete RDP stack works on Hyper-V with KDE/KWin:

```
mstsc → TCP:3389 → TLS → IronRDP (lamco) → XDG Desktop Portal → KWin ScreenCast
    → PipeWire (private Portal FD) → MemFd buffers → openh264 software H.264 → EGFX → mstsc
```

**Performance:** ~21 FPS at 1920x1080 (2 vCPUs, software encoding, MemFd buffers)

### Critical KWin Patch Required

**`screencast-shm-fallback.patch`** — KWin's `newStreamParams()` advertises only
DmaBuf as the buffer type when DmaBuf is negotiated. On Hyper-V's virtual GPU
(llvmpipe software renderer), `DmaBufScreenCastBuffer::create()` fails at EGL
import time. Without MemFd in the advertised types, `onStreamAddBuffer` cannot
fall back, leaving `pwBuffer->user_data` null and causing an infinite
format-negotiation loop.

**Fix:** Change `buffertypes` to include both DmaBuf and MemFd:
```cpp
// Before (broken on virtual GPUs):
const int buffertypes = m_dmabufParams ? (1 << SPA_DATA_DmaBuf) : (1 << SPA_DATA_MemFd);

// After (falls back to MemFd when DmaBuf fails):
const int buffertypes = m_dmabufParams ? ((1 << SPA_DATA_DmaBuf) | (1 << SPA_DATA_MemFd)) : (1 << SPA_DATA_MemFd);
```

**Upstream status:** Patch prepared in `c:\repos\kwin` for submission to KDE.
VMCreate deploys it via `InstallLamcoRdpStep.cs` (builds patched screencast plugin).

### DmaBuf Not Available on Hyper-V

DmaBuf buffers are NOT available on Hyper-V, even on the shared PipeWire daemon.
KWin negotiates with `modifier=0x0` and allocates MemFd buffers even when the
consumer requests DmaBuf with `DRM_FORMAT_MOD_LINEAR`. This is because the
Hyper-V virtual GPU is `llvmpipe` (software rasterizer) — it cannot export DmaBuf.

**Implication:** Zero-copy is not possible. The bridge daemon (built but not
needed) was designed for DmaBuf relay, but since there are no DmaBuf frames,
it provides no benefit over the current direct MemFd path.

### Dynamic Resolution (kscreen-doctor)

Implemented dynamic RDP resolution for KDE/KWin:
- `change_compositor_resolution()` in lamco calls `kscreen-doctor` to change
  KWin's output mode, with `find_best_mode()` to pick the closest supported DRM mode
- `request_initial_size()` returns current compositor size (no-op) to prevent
  RDP desktop vs compositor mismatch
- `request_layout()` calls `change_compositor_resolution()` for runtime window resize
- VMCreate sets compositor to 1920x1080 before lamco starts via kscreen-doctor autostart
- `cursor_mode = "embedded"` fixes double cursor and trail

### What Doesn't Work

| Feature | Status | Reason |
|---------|--------|--------|
| DmaBuf zero-copy | ❌ | llvmpipe can't export DmaBuf |
| VAAPI hardware encoding | ❌ | No real GPU on Hyper-V |
| xdg-desktop-portal-generic | ❌ | KWin 6.3.6 lacks ext-image-copy-capture-v1 |
| Hyper-V Enhanced Session | ❌ | IronRDP server lacks PCB/HvSocket support |
| Reconnect without restart | ⚠️ | PipeWire client-node.c resource==NULL bug on private FD |

### Hyper-V Enhanced Session

IronRDP has **client-side** VMConnect support (`ironrdp-vmconnect` crate, PR #1505)
using TCP port 2179 with Preconnection Blob (PCB) V2. However, the **server-side**
support is missing:

1. **No vsock/HvSocket listener** — lamco only listens on TCP
2. **No PCB parsing on server** — `ironrdp-acceptor` starts with X.224 directly
3. **No pre-X.224 CredSSP ordering** — required by Enhanced Session protocol
4. **No `ironrdp-vmconnect-server` crate** — would need to be created

VMCreate already sets `EnhancedSessionTransportType = HvSocket` on the VM and
lamco's systemd unit allows `AF_VSOCK` — only the server-side implementation
is missing. This would be a significant feature for Hyper-V usability.