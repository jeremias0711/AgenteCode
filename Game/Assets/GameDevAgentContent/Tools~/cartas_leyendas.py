import sys
import numpy as np
from PIL import Image, ImageFilter
S, OUT = sys.argv[1], sys.argv[2]
def luma(c): return 0.3*c[...,0]+0.59*c[...,1]+0.11*c[...,2]
def medfilt(v, k=9):
    v=np.array(v,float); out=v.copy(); n=len(v)
    for i in range(n):
        win=v[max(0,i-k):min(n,i+k+1)]; win=win[~np.isnan(win)]
        out[i]=np.median(win) if len(win) else np.nan
    return out
for who in ('mohan','madremonte','hojarasquin','patasola'):
    a=np.asarray(Image.open(f'{S}/{who}_512.png').convert('RGB')).astype(np.float32); h,w,_=a.shape
    L=luma(a)
    gold=(a[...,0]>130)&(a[...,1]>95)&(a[...,2]<140)&(a[...,0]-a[...,2]>45)
    # frame: the two long gold sides
    cols=gold[120:620].sum(0); side=np.where(cols>120)[0]
    fl,fr=side.min(),side.max()
    rows=gold[:,fl:fr+1].sum(1); top_band=np.where(rows[:200]>0.6*(fr-fl))[0]; bot_band=np.where(rows[400:]>0.6*(fr-fl))[0]+400
    ft,fb=top_band.min(),bot_band.max()
    # plate: gold rows above the frame
    pl,pr,pt=[],[],None
    for y in range(0,ft):
        xs=np.where(gold[y,90:w-90])[0]+90
        if len(xs)>=6: pl.append(xs.min()); pr.append(xs.max()); pt=y if pt is None else pt
        else: pl.append(np.nan); pr.append(np.nan)
    pl,pr=medfilt(pl,4),medfilt(pr,4)
    # ---- cost plate interior: rectangle between the plate's inner gold edges, repainted by interpolation
    px0=int(np.nanmedian(pl))+16; px1=int(np.nanmedian(pr))-16
    py0=pt+10; py1=ft-6
    for y in range(py0,py1):
        lc=np.median(a[y,px0-4:px0],axis=0); rc=np.median(a[y,px1:px1+4],axis=0)
        t=np.linspace(0,1,px1-px0)[:,None]
        a[y,px0:px1]=lc*(1-t)+rc*t
    sub=Image.fromarray(a.astype(np.uint8)).crop((px0-3,py0-3,px1+3,py1+3)).filter(ImageFilter.GaussianBlur(1.2))
    a[py0-3:py1+3,px0-3:px1+3]=np.asarray(sub).astype(np.float32)
    # ---- alpha: plate rows + frame rows, smoothed extents, nothing inside the card is ever removed
    left=np.full(h,np.nan); right=np.full(h,np.nan)
    for y in range(0,ft):
        left[y],right[y]=pl[y],pr[y]
    for y in range(ft,fb+1):
        xs=np.where(gold[y,max(0,fl-14):min(w,fr+15)])[0]+max(0,fl-14)
        if len(xs)>=2: left[y],right[y]=xs.min(),xs.max()
    left[ft:fb+1]=medfilt(left[ft:fb+1],6); right[ft:fb+1]=medfilt(right[ft:fb+1],6)
    alpha=np.zeros((h,w),np.uint8)
    for y in range(h):
        if np.isnan(left[y]) or np.isnan(right[y]): continue
        alpha[y,int(left[y]):int(right[y])+1]=255
    am=Image.fromarray(alpha).filter(ImageFilter.GaussianBlur(0.7))
    out=Image.fromarray(np.dstack([a.clip(0,255).astype(np.uint8),np.asarray(am)]),'RGBA')
    bbox=out.getchannel('A').point(lambda v:255 if v>8 else 0).getbbox()
    out=out.crop(bbox); out.save(f'{OUT}/carta2_{who}.png',optimize=True)
    # plate interior rectangle in the cropped sprite (normalised), for the cost text
    print(who,'size',out.size,'plate_rect_norm',round((px0-bbox[0])/out.width,4),round((py0-bbox[1])/out.height,4),round((px1-bbox[0])/out.width,4),round((py1-bbox[1])/out.height,4))
