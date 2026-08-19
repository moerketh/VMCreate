#include <stdio.h>
#include <fcntl.h>
#include <unistd.h>
#include <gbm.h>
#include <EGL/egl.h>
#include <EGL/eglext.h>
#define EGL_LINUX_DMA_BUF_EXT 0x3270
#define EGL_LINUX_DRM_FOURCC_EXT 0x3271
#define EGL_DMA_BUF_PLANE0_FD_EXT 0x3272
#define EGL_DMA_BUF_PLANE0_OFFSET_EXT 0x3273
#define EGL_DMA_BUF_PLANE0_PITCH_EXT 0x3274

int main(){
    // Step 1: Create dumb buffer on card0, export as DMA-BUF fd
    int card0 = open("/dev/dri/card0", O_RDWR|O_CLOEXEC);
    if (card0 < 0) { perror("card0"); return 1; }

    // Use DRM ioctl directly for dumb buffer
    #include <xf86drm.h>
    struct drm_mode_create_dumb create = {.height=64, .width=64, .bpp=32};
    if (drmIoctl(card0, DRM_IOCTL_MODE_CREATE_DUMB, &create)) { perror("CREATE_DUMB"); return 1; }
    printf("card0 dumb: handle=%u pitch=%u\n", create.handle, create.pitch);

    int primefd = -1;
    drmPrimeHandleToFD(card0, create.handle, DRM_CLOEXEC|DRM_RDWR, &primefd);
    printf("prime fd=%d\n", primefd);

    // Step 2: Open renderD128, create GBM device
    int render = open("/dev/dri/renderD128", O_RDWR|O_CLOEXEC);
    struct gbm_device *rgbm = gbm_create_device(render);
    printf("renderD128 gbm=%p\n", rgbm);

    // Step 3: Import the card0 DMA-BUF fd into renderD128's GBM as a bo
    struct gbm_import_fd_data idata = {.fd=primefd, .width=64, .height=64, .stride=create.pitch, .format=0x34325258};
    struct gbm_bo *bo = gbm_bo_import(rgbm, GBM_BO_IMPORT_FD, &idata, GBM_BO_USE_RENDERING);
    printf("gbm_bo_import(FD) on renderD128 = %p\n", bo);

    if (bo) {
        // Step 4: Try EGL_NATIVE_PIXMAP_KHR on renderD128's EGL display
        EGLDisplay dpy = eglGetPlatformDisplay(EGL_PLATFORM_GBM_KHR, rgbm, NULL);
        EGLint maj, min;
        eglInitialize(dpy, &maj, &min);
        printf("eglInit %d.%d\n", maj, min);

        PFNEGLCREATEIMAGEKHRPROC ci = (void*)eglGetProcAddress("eglCreateImageKHR");

        // Try EGL_NATIVE_PIXMAP_KHR with the imported bo
        EGLImageKHR img = ci(dpy, EGL_NO_CONTEXT, EGL_NATIVE_PIXMAP_KHR, bo, NULL);
        printf("EGL_NATIVE_PIXMAP_KHR (card0 dumb -> renderD128 gbm) = %p err=0x%x\n", img, eglGetError());

        // Also try EGL_LINUX_DMA_BUF with the same fd
        EGLint attrs[] = {EGL_WIDTH,64, EGL_HEIGHT,64, EGL_LINUX_DRM_FOURCC_EXT,0x34325258,
                          EGL_DMA_BUF_PLANE0_FD_EXT,primefd, EGL_DMA_BUF_PLANE0_OFFSET_EXT,0,
                          EGL_DMA_BUF_PLANE0_PITCH_EXT,create.pitch, EGL_NONE};
        EGLImageKHR img2 = ci(dpy, EGL_NO_CONTEXT, EGL_LINUX_DMA_BUF_EXT, NULL, attrs);
        printf("EGL_LINUX_DMA_BUF (card0 fd -> renderD128 egl) = %p err=0x%x\n", img2, eglGetError());

        eglTerminate(dpy);
        gbm_bo_destroy(bo);
    }
    gbm_device_destroy(rgbm);
    close(render);
    close(primefd);
    struct drm_mode_destroy_dumb dd = {.handle=create.handle};
    drmIoctl(card0, DRM_IOCTL_MODE_DESTROY_DUMB, &dd);
    close(card0);
    return 0;
}