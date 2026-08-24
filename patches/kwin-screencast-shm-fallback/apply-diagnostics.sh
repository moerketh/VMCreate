#!/bin/bash
# Instrument DmaBufScreenCastBuffer::create() + ScreenCastStream negotiation
# to identify WHICH early return fails on Hyper-V hyperv_drm.
# Applied to a CLEAN 6.3.6 tree (no SHM fallback) for an uncontaminated read.
set -e
cd /home/vmcreate/kwin-src/kwin-6.3.6

# Restore pristine files in case of a previous partial run
tar xf ../kwin_6.3.6.orig.tar.xz -C . \
    kwin-6.3.6/src/plugins/screencast/screencastbuffer.cpp \
    kwin-6.3.6/src/plugins/screencast/screencaststream.cpp \
    --strip-components=1 2>/dev/null || true

# --- screencastbuffer.cpp: log every early return in create() ---
python3 - <<'PYEOF'
import re

p = 'src/plugins/screencast/screencastbuffer.cpp'
s = open(p).read()

if '#include "kwinscreencast_logging.h"' not in s:
    s = s.replace('#include "screencastbuffer.h"',
                  '#include "screencastbuffer.h"\n#include "kwinscreencast_logging.h"', 1)

# (anchor, log statement valid *inside* that anchor's if-block scope)
fails = [
    ('''    AbstractEglBackend *backend = dynamic_cast<AbstractEglBackend *>(Compositor::self()->backend());
    if (!backend || !backend->drmDevice()) {
        return nullptr;
    }''',
     'qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG create() fail A: no EGL backend or drmDevice";'),

    ('''    GraphicsBuffer *buffer = backend->drmDevice()->allocator()->allocate(options);
    if (!buffer) {
        return nullptr;
    }''',
     '''qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG create() fail B: allocator allocate() failed" \\
        << " size=" << options.size.width() << "x" << options.size.height() \\
        << " software=" << options.software;'''),

    ('''    const DmaBufAttributes *attrs = buffer->dmabufAttributes();
    if (!attrs) {
        buffer->drop();
        return nullptr;
    }''',
     'qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG create() fail C: buffer has no dmabufAttributes" << " n_datas=" << pwBuffer->buffer->n_datas;'),

    ('''    const void *syncTimelineMeta = spa_buffer_find_meta_data(pwBuffer->buffer, SPA_META_SyncTimeline, sizeof(spa_meta_sync_timeline));
    if (pwBuffer->buffer->n_datas != uint32_t(attrs->planeCount + (syncTimelineMeta ? 2 : 0))) {
        buffer->drop();
        return nullptr;
    }''',
     '''qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG create() fail D: n_datas mismatch" \\
        << " planeCount=" << attrs->planeCount << " n_datas=" << pwBuffer->buffer->n_datas \\
        << " syncTimelineMeta=" << (syncTimelineMeta != nullptr) \\
        << " mod=0x" << Qt::hex << attrs->modifier << Qt::dec;'''),

    ('''    backend->makeCurrent();

    auto texture = backend->importDmaBufAsTexture(*attrs);
    if (!texture) {
        buffer->drop();
        return nullptr;
    }''',
     '''qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG create() fail E: EGL importDmaBufAsTexture failed" \\
        << " planeCount=" << attrs->planeCount << " n_datas=" << pwBuffer->buffer->n_datas \\
        << " syncTimelineMeta=" << (syncTimelineMeta != nullptr) \\
        << " mod=0x" << Qt::hex << attrs->modifier << Qt::dec;'''),

    ('''    auto framebuffer = std::make_unique<GLFramebuffer>(texture.get());
    if (!framebuffer->valid()) {
        buffer->drop();
        return nullptr;
    }''',
     '''qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG create() fail F: GLFramebuffer invalid" \\
        << " planeCount=" << attrs->planeCount << " n_datas=" << pwBuffer->buffer->n_datas \\
        << " syncTimelineMeta=" << (syncTimelineMeta != nullptr);'''),

    ('''        const FileDescriptor &syncobjfd = synctimeline->fileDescriptor();
        if (!syncobjfd.isValid()) {
            buffer->drop();
            return nullptr;
        }''',
     'qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG create() fail G: SyncTimeline fd invalid";'),
]

for anchor, log in fails:
    assert anchor in s, 'anchor not found: ' + log[:40]
    assert s.count(anchor) == 1, 'anchor not unique: ' + log[:40]
    lines = anchor.split('\n')
    if_idx = next(i for i, l in enumerate(lines) if l.strip().startswith('if '))
    insert = '\n'.join('    ' + l if l else l for l in log.split('\n'))
    new = '\n'.join(lines[:if_idx + 1] + [insert] + lines[if_idx + 1:])
    s = s.replace(anchor, new, 1)

open(p, 'w').write(s)
print('screencastbuffer.cpp instrumented')
PYEOF

# --- screencaststream.cpp: log negotiation context ---
python3 - <<'PYEOF'
p = 'src/plugins/screencast/screencaststream.cpp'
s = open(p).read()

# 0. init(): why does the AbstractEglBackend cast fail?
anchor0 = '''    AbstractEglBackend *backend = qobject_cast<AbstractEglBackend *>(Compositor::self()->backend());
    if (!backend) {
        m_error = i18n("OpenGL compositing is required for screencasting");
        return false;
    }'''
assert anchor0 in s
s = s.replace(anchor0, '''    AbstractEglBackend *backend = qobject_cast<AbstractEglBackend *>(Compositor::self()->backend());
    if (!backend) {
        qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG init(): backend cast failed"
            << " compositor=" << (Compositor::self() != nullptr)
            << " rawBackend=" << (Compositor::self() && Compositor::self()->backend() ? Compositor::self()->backend()->metaObject()->className() : "<null>");
        m_error = i18n("OpenGL compositing is required for screencasting");
        return false;
    }''', 1)

# 1. newStreamParams: log negotiated buffertypes, blocks, syncobj offer
anchor1 = '''    qCDebug(KWIN_SCREENCAST) << objectName() << "announcing stream params. with dmabuf:" << m_dmabufParams.has_value();
    const int buffertypes = m_dmabufParams ? (1 << SPA_DATA_DmaBuf) : (1 << SPA_DATA_MemFd);'''
assert anchor1 in s
s = s.replace(anchor1, '''    qCDebug(KWIN_SCREENCAST) << objectName() << "announcing stream params. with dmabuf:" << m_dmabufParams.has_value();
    qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG newStreamParams: dmabufParams=" << m_dmabufParams.has_value()
        << " planeCount=" << (m_dmabufParams ? m_dmabufParams->planeCount : -1)
        << " m_modifiers.count=" << m_modifiers.count();
    const int buffertypes = m_dmabufParams ? (1 << SPA_DATA_DmaBuf) : (1 << SPA_DATA_MemFd);''', 1)

# 2. onStreamParamChanged: log received modifiers + testCreateDmaBuf outcome
anchor2 = '''            qCDebug(KWIN_SCREENCAST) << objectName() << "Stream dmabuf modifiers received, offering our best suited modifier" << m_dmabufParams.has_value();'''
assert anchor2 in s
s = s.replace(anchor2, '''            qCDebug(KWIN_SCREENCAST) << objectName() << "Stream dmabuf modifiers received, offering our best suited modifier" << m_dmabufParams.has_value();
            {
                QString mods;
                for (uint64_t modifier : receivedModifiers) {
                    mods += QString("0x%1 ").arg(modifier, 0, 16);
                }
                qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG paramChanged: received" << receivedModifiers.count() << "modifiers:" << mods
                    << "-> testCreateDmaBuf=" << (m_dmabufParams ? "OK" : "FAIL");
            }''', 1)

# 3. onStreamAddBuffer entry: log incoming spa_data types
anchor3 = '''    struct spa_data *spa_data = pwBuffer->buffer->datas;
    if (spa_data[0].type & (1 << SPA_DATA_DmaBuf)) {'''
assert s.count(anchor3) == 1
s = s.replace(anchor3, '''    struct spa_data *spa_data = pwBuffer->buffer->datas;
    qCWarning(KWIN_SCREENCAST) << "DMABUF-DIAG addBuffer: n_datas=" << pwBuffer->buffer->n_datas
        << " type[0]=0x" << Qt::hex << spa_data[0].type << Qt::dec
        << " datas[1].type=0x" << Qt::hex << (pwBuffer->buffer->n_datas > 1 ? pwBuffer->buffer->datas[1].type : 0) << Qt::dec;
    if (spa_data[0].type & (1 << SPA_DATA_DmaBuf)) {''', 1)

open(p, 'w').write(s)
print('screencaststream.cpp instrumented')
PYEOF
echo "=== Instrumentation applied. Now build. ==="
