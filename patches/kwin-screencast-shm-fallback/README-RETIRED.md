# KWin screencast patches — STATUS: RETIRED (2026-08-24)

The black screen on Hyper-V/KDE is fixed **entirely on the lamco-rdp-server
side**. No KWin patch is shipped or needed anymore:

- `InstallLamcoRdpStep.cs` now builds lamco-rdp-server from the fork
  `moerketh/lamco-rdp-server@feature/hyperv-enhanced-session`, which:
  1. brackets DMA-BUF CPU reads with `DMA_BUF_IOCTL_SYNC` (correct
     `_IOW('b', 0, 8)` = `0x40086200` number), maps at pgoff 0, and
     rejects non-linear modifiers
  2. materializes DmaBuf frames to CPU memory before the frame cache
  3. falls back to MemFd + rebinds the stream when DmaBuf negotiation
     yields zero frames for 10s (measurement-driven, no driver allowlist)
- Stock KWin 6.3.6 works with that binary. Verified working end-to-end on
  the TEST VM 2026-08-24 (frames at 1920x1080 over RDP).

## Files kept for archaeology / upstream reference
- `01-shm-fallback.patch` — the original (now retired) dataType-mask patch.
  Superseded: see below.
- `02-test-import.patch` — draft testCreateDmaBuf EGL-import idea (never
  needed; note upstream master already builds a GLFramebuffer there via
  commit 769e3365c4).
- `apply-diagnostics.sh` — KWin 6.3.6 instrumentation used to diagnose the
  negotiation (logs every early return in DmaBufScreenCastBuffer::create(),
  the onStreamParamChanged guard, addBuffer spa_data types). Useful again
  if the KWin-side modifier livelock is pursued upstream:
- `vm-build-install.sh` — builds the instrumented plugin in Release mode
  (Debug builds break qobject_cast across Qt ABI — see memory notes).
- `cross_device_test.c`, `egl_native_pixmap_test.c` — earlier probes.

## Known-but-unfixed (optional upstream work)
KWin 6.3.6 has a modifier negotiation livelock: when a consumer fixates
modifier 0x0 (LINEAR) and the GBM allocator returns
DRM_FORMAT_MOD_INVALID for the allocated buffer, the guard
`!receivedModifiers.contains(m_dmabufParams->modifier)` re-triggers
forever (~1000 re-offers/sec, CPU burn only — the stream still negotiates
eventually). Diagnosed on the instrumented build; evidence log was at
/tmp/dmabuf-diag-evidence.log on the VM. Candidate fix: treat INVALID as
satisfying a LINEAR fixation (see gbmgraphicsbufferallocator.cpp — the
gbm_bo_create fallback path already normalizes this way).
