"""Offline PyAV decoding only, no ffmpeg.exe and no production dependency."""
import hashlib
import itertools
import json
import math
import sys
import numpy as np
import backend_compare as b
import validate as v
sys.path.insert(0, str(v.ROOT / '.ai-tmp/cache/backend-validation-python'))
import av

def run(label):
    root = b.ROOT / label
    summary = v.read(root / 'summary.json')
    source = v.ROOT / summary['source']['path']
    results = []
    for item in summary['retainedOutputs']:
        output = v.ROOT / item['path']
        rows=[]
        with av.open(str(source)) as src, av.open(str(output)) as dst:
            for index, (a,z) in enumerate(itertools.zip_longest(src.decode(video=0),dst.decode(video=0))):
                assert a is not None and z is not None, 'Decoded frame count differs'
                # Compare RGB display values at output stored dimensions, no autorotation.
                x=a.reformat(width=z.width,height=z.height,format='rgb24').to_ndarray()
                y=z.to_ndarray(format='rgb24')
                delta=x.astype(np.float64)-y.astype(np.float64)
                mse=float(np.mean(delta*delta))
                rows.append(dict(index=index,sourceDimensions=[a.width,a.height],outputDimensions=[z.width,z.height],
                                 mse=mse,mae=float(np.mean(np.abs(delta))),
                                 sourceMean=float(x.mean()),outputMean=float(y.mean())))
        mse=sum(x['mse'] for x in rows)/len(rows)
        results.append(dict(output=item,frames=rows,frameCount=len(rows),meanMse=mse,
                            psnrRgbDisplayDb=None if mse==0 else 10*math.log10(255*255/mse)))
    v.save(root/'pixel-observations.json',dict(validator='PyAV decode only + numpy RGB display comparison',
        pyavVersion=av.__version__,libraries=av.library_versions,numpyVersion=np.__version__,results=results,
        acceptance='Observations, not frozen P5 PSNR/SSIM metric or a quality PASS. Same format tags are not alone proof of correct pixels.'))
    print(json.dumps([(x['frameCount'],x['psnrRgbDisplayDb']) for x in results]))

if __name__=='__main__': run(sys.argv[1])
