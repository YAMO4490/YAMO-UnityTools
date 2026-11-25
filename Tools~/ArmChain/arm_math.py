"""Rig-independent hand goals in Max's measured row-quaternion convention.

No NumPy dependency: one bounded eight-bone pose buffer is kept per motion.
"""
import array
import math
import struct
from pathlib import Path

SUFFIXES = [f" {side} {bone}" for side in ("L", "R")
            for bone in ("Clavicle", "UpperArm", "Forearm", "Hand")]


def add(a, b): return tuple(x + y for x, y in zip(a, b))
def sub(a, b): return tuple(x - y for x, y in zip(a, b))
def mul(a, s): return tuple(x * s for x in a)
def dot(a, b): return sum(x * y for x, y in zip(a, b))
def length(a): return math.sqrt(dot(a, a))
def unit(a): return mul(a, 1.0 / max(length(a), 1e-12))
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def angle(a, b): return math.degrees(math.acos(max(-1, min(1, dot(unit(a), unit(b))))))
def qangle(a, b): return math.degrees(2*math.acos(max(0, min(1, abs(dot(unit(a), unit(b)))))))


def qmul(a, b):
    x,y,z,w = a; X,Y,Z,W = b
    return (w*X+x*W+y*Z-z*Y, w*Y-x*Z+y*W+z*X,
            w*Z+x*Y-y*X+z*W, w*W-x*X-y*Y-z*Z)


class Poses:
    def __init__(self, frames, data=None):
        self.frames = frames
        self.data = array.array('f', data) if data is not None else array.array('f', [0]) * (frames*56)
        if len(self.data) != frames*56 or any(not math.isfinite(x) for x in self.data):
            raise ValueError('Invalid arm pose buffer')

    def bone(self, f, b):
        i = f*56+b*7
        return self.data[i:i+7]

    def pos(self, f, b): return tuple(self.bone(f,b)[:3])
    def quat(self, f, b): return tuple(self.bone(f,b)[3:])

    def set_pos(self, f, b, p):
        i=f*56+b*7; self.data[i:i+3]=array.array('f',p)

    def set_quat(self, f, b, q):
        i=f*56+b*7+3; self.data[i:i+4]=array.array('f',q)

    def save(self, path):
        path=Path(path); partial=path.with_suffix(path.suffix+'.partial')
        with partial.open('wb') as f:
            f.write(struct.pack('<ii',self.frames,56)); self.data.tofile(f)
        partial.replace(path)

    @classmethod
    def load(cls, path):
        with Path(path).open('rb') as f:
            frames,columns=struct.unpack('<ii',f.read(8))
            if columns!=56 or not 1<=frames<=50001: raise ValueError('Invalid pose header')
            a=array.array('f'); a.fromfile(f,frames*56)
            if f.read(1): raise ValueError('Unexpected trailing pose data')
        return cls(frames,a)


def stats(values):
    values=sorted(values)
    if not values: return dict(median=0,p95=0,max=0)
    def p(q):
        k=(len(values)-1)*q; i=int(k)
        return values[i]+(values[min(i+1,len(values)-1)]-values[i])*(k-i)
    return dict(median=p(.5),p95=p(.95),max=values[-1])


def check_tpose(ref):
    for b in (0,4):
        u=sub(ref.pos(0,b+2),ref.pos(0,b+1));f=sub(ref.pos(0,b+3),ref.pos(0,b+2))
        total=length(u)+length(f)
        if total<1e-6 or angle(u,f)>15 or abs(sub(ref.pos(0,b+3),ref.pos(0,b+1))[2])>total*.2:
            raise ValueError('Reference is not a straight horizontal T-pose; choose the correct raw T-pose frame')


def make_targets(raw, baseline, raw_reference, target_reference):
    if raw.frames!=baseline.frames: raise ValueError('Raw/body sample counts differ')
    check_tpose(raw_reference);check_tpose(target_reference)
    target=Poses(baseline.frames,baseline.data);scales=[]
    for b in (0,4):
        rl=length(sub(raw_reference.pos(0,b+2),raw_reference.pos(0,b+1)))+length(sub(raw_reference.pos(0,b+3),raw_reference.pos(0,b+2)))
        tl=length(sub(baseline.pos(0,b+2),baseline.pos(0,b+1)))+length(sub(baseline.pos(0,b+3),baseline.pos(0,b+2)))
        ref_len=length(sub(target_reference.pos(0,b+2),target_reference.pos(0,b+1)))+length(sub(target_reference.pos(0,b+3),target_reference.pos(0,b+2)))
        if abs(tl/ref_len-1)>.015: raise ValueError('Unity target and Base MAX arm lengths do not match')
        scale=tl/rl;scales.append(scale)
        rq=unit(raw_reference.quat(0,b+3)); offset=qmul(unit(target_reference.quat(0,b+3)),(-rq[0],-rq[1],-rq[2],rq[3]))
        for f in range(raw.frames):
            shoulder=baseline.pos(f,b+1)
            target.set_pos(f,b+2,add(shoulder,mul(sub(raw.pos(f,b+2),raw.pos(f,b+1)),scale)))
            target.set_pos(f,b+3,add(shoulder,mul(sub(raw.pos(f,b+3),raw.pos(f,b+1)),scale)))
            target.set_quat(f,b+3,unit(qmul(offset,unit(raw.quat(f,b+3)))))
    return target,scales


def stabilize_poles(target, half_window=4):
    stable=Poses(target.frames,target.data);windows=[];skipped=[]
    for arm,b in enumerate((0,4)):
        axes=[];bends=[];radii=[];flex=[]
        for f in range(target.frames):
            s=target.pos(f,b+1);e=target.pos(f,b+2);h=target.pos(f,b+3)
            z=unit(sub(h,s));v=sub(e,s);v=sub(v,mul(z,dot(v,z)))
            axes.append(z);bends.append(unit(v));radii.append(length(v));flex.append(angle(sub(e,s),sub(h,e)))
        bad=[f for f in range(3,target.frames) if angle(bends[f-1],bends[f])>90 and min(flex[f-1],flex[f])<8]
        spans=[]
        for f in bad:
            lo=max(2,f-half_window);hi=min(target.frames-1,f+half_window)
            if spans and lo<=spans[-1][1]: spans[-1][1]=max(spans[-1][1],hi)
            else: spans.append([lo,hi])
        for lo,hi in spans:
            if max(flex[lo:hi+1])>80:
                skipped.append(dict(arm=arm,first=lo,last=hi,reason='Large source discontinuity retained'));continue
            shift=0
            for f in range(lo+1,hi):
                z=axes[f];a=unit(sub(bends[lo],mul(z,dot(bends[lo],z))));c=unit(sub(bends[hi],mul(z,dot(bends[hi],z))))
                if min(length(a),length(c))<.5: continue
                theta=math.atan2(dot(z,cross(a,c)),dot(a,c))*(f-lo)/(hi-lo)
                v=add(mul(a,math.cos(theta)),mul(cross(z,a),math.sin(theta)))
                s=target.pos(f,b+1);e=target.pos(f,b+2)
                p=add(add(s,mul(z,dot(sub(e,s),z))),mul(v,radii[f]))
                stable.set_pos(f,b+2,p);shift=max(shift,length(sub(p,e)))
            windows.append(dict(arm=arm,first=lo,last=hi,maxElbowShiftUnits=shift))
    return stable,dict(windows=windows,skippedSourceDiscontinuities=skipped,handGoalsUnchanged=all(stable.bone(f,b)==target.bone(f,b) for f in range(target.frames) for b in (3,7)))


def compare(a,b,units_per_cm=1):
    if a.frames!=b.frames:raise ValueError('Pose comparison length mismatch')
    hp=[];hr=[];allp=[];allr=[]
    for f in range(a.frames):
        for i in range(8):
            p=length(sub(a.pos(f,i),b.pos(f,i)))/units_per_cm;r=qangle(a.quat(f,i),b.quat(f,i))
            allp.append(p);allr.append(r)
            if i in (3,7):hp.append(p);hr.append(r)
    return dict(handPositionCm=stats(hp),handRotationDeg=stats(hr),allArmPositionCm=stats(allp),allArmRotationDeg=stats(allr))
