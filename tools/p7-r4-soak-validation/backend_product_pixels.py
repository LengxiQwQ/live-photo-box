"""Read retained genuine Core output and current outputs, preserving identities."""
import sys
import numpy as np
import backend_compare as b
import validate as v
sys.path.insert(0,str(v.ROOT/'.ai-tmp/cache/backend-validation-python'))
import av

def run():
    paths=[b.SOURCES['hevc'],b.SOURCES['h264'],
           b.ROOT/'repeat-mf-hevc-h264/outputs/3.mp4',
           b.ROOT/'repeat-mf-hevc-hevc/outputs/3.mp4',
           b.ROOT/'qualify-libav-exact-h264/outputs/3.mov']
    rows=[]
    for path in paths:
        before=v.identity(path)
        with av.open(str(path)) as container:
            frame=next(container.decode(video=0));rgb=frame.to_ndarray(format='rgb24')
            rows.append(dict(input=before,firstFrameRgbMean=np.mean(rgb,axis=(0,1)).tolist(),
                             dimensions=[frame.width,frame.height],pts=frame.pts,timeBase=str(frame.time_base)))
        assert before==v.identity(path)
    v.save(b.ROOT/'existing-product-pixel-confirmation.json',dict(
        diagnosticOnly=True,pyavVersion=av.__version__,libraries=av.library_versions,
        meaning='Prior genuine Core codec output and current MF outputs share visibly green pixels; independent display decoding observation, root cause not isolated.',rows=rows))

if __name__=='__main__':run()
