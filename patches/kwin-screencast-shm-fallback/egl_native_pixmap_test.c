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
    int card0=open("/dev/dri/card0",O_RDWR|O_CLOEXEC);
    if(card0<0){perror("card0");return 1;}
    struct gbm_device *gdev=gbm_create_device(card0);
    if(!gdev){printf("gbm_create_device(card0) FAILED\n");return 1;}
    printf("gbm device on card0=%p\n",gdev);

    struct gbm_bo *bo=gbm_bo_create(gdev,64,64,0x34325258,GBM_BO_USE_RENDERING|GBM_BO_USE_LINEAR);
    printf("gbm_bo RENDERING|LINEAR=%p\n",bo);
    if(!bo) bo=gbm_bo_create(gdev,64,64,0x34325258,GBM_BO_USE_RENDERING);
    if(!bo) printf("gbm_bo RENDERING only failed\n");
    if(!bo) bo=gbm_bo_create(gdev,64,64,0x34325258,0);
    if(!bo) printf("gbm_bo no-flags failed\n");

    if(bo){
        EGLDisplay dpy=eglGetPlatformDisplay(EGL_PLATFORM_GBM_KHR,gdev,NULL);
        EGLint maj,min;
        if(!eglInitialize(dpy,&maj,&min)){printf("eglInit FAILED\n");return 1;}
        printf("eglInit %d.%d\n",maj,min);

        PFNEGLCREATEIMAGEKHRPROC ci=(void*)eglGetProcAddress("eglCreateImageKHR");

        EGLImageKHR img=ci(dpy,EGL_NO_CONTEXT,EGL_NATIVE_PIXMAP_KHR,bo,NULL);
        printf("EGL_NATIVE_PIXMAP_KHR=%p err=0x%x\n",img,eglGetError());

        int bofd=gbm_bo_get_fd(bo);
        int stride=gbm_bo_get_stride(bo);
        printf("bo fd=%d stride=%d\n",bofd,stride);

        EGLint attrs[]={EGL_WIDTH,64,EGL_HEIGHT,64,EGL_LINUX_DRM_FOURCC_EXT,0x34325258,
                        EGL_DMA_BUF_PLANE0_FD_EXT,bofd,EGL_DMA_BUF_PLANE0_OFFSET_EXT,0,
                        EGL_DMA_BUF_PLANE0_PITCH_EXT,stride,EGL_NONE};
        EGLImageKHR img2=ci(dpy,EGL_NO_CONTEXT,EGL_LINUX_DMA_BUF_EXT,NULL,attrs);
        printf("EGL_LINUX_DMA_BUF(from card0 bo)=%p err=0x%x\n",img2,eglGetError());

        eglTerminate(dpy);
        gbm_bo_destroy(bo);
    }
    gbm_device_destroy(gdev);
    close(card0);
    return 0;
}