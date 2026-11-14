"""Dedicated 3dsmaxbatch worker. Never attach to a user's interactive Max.

Usage: a generated wrapper sets sys.argv[1] to one job.json then run_path's this file.
All intermediate files remain in the job directory; only verified files are promoted by Unity.
"""
import json,math,os,sys,time,traceback,hashlib
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parent))
from arm_math import Poses,SUFFIXES,make_targets,stabilize_poles,compare,stats,length,sub,dot,angle,unit,mul
import pymxs
rt=pymxs.runtime


def sha(path):
    h=hashlib.sha256()
    with open(path,'rb') as f:
        for block in iter(lambda:f.read(4*1024*1024),b''):h.update(block)
    return h.hexdigest()


def atomic_json(path,value):
    path=Path(path);temp=path.with_suffix(path.suffix+'.partial')
    temp.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8');temp.replace(path)


def run(job):
    work=Path(job['workDirectory']);work.mkdir(parents=True,exist_ok=True)
    def progress(stage):
        atomic_json(work/'progress.json',dict(token=job['token'],pid=os.getpid(),stage=stage,time=time.time()))
    def checked(value,expected,stage):
        if value!=expected:raise RuntimeError(stage+': '+str(value))
    progress('Loading native arm writer')
    rt.setQuietMode(True)
    rt.loadDllsFromDir(str(Path(job['nativePlugin']).parent)+'\\',Path(job['nativePlugin']).name)
    rate=int(job['sampleRate']);end=int(job['endFrame']);root_name=job['bipedRoot'];names=[root_name+s for s in SUFFIXES]
    source_hash=sha(job['rawFbx']);base_hash=sha(job['baseMax'])
    if source_hash!=job['rawSha256'] or base_hash!=job['baseSha256']:raise RuntimeError('Source or Base MAX changed before processing')
    if not rt.loadMaxFile(job['baseMax'],useFileUnits=True,quiet=True):raise RuntimeError('Cannot load Base MAX')
    units=rt.units.SystemType;unit_scale=float(rt.units.SystemScale);units_per_cm=float(rt.units.decodeValue('1cm'))
    def reset():
        rt.resetMaxFile(rt.Name('noPrompt'));rt.frameRate=rate;rt.units.SystemType=units;rt.units.SystemScale=unit_scale
    def find(n):
        node=rt.getNodeByName(n,exact=True)
        if node is None:raise RuntimeError('Missing/renamed FBX bone: '+n)
        return node
    def import_fbx(path,mode):
        rt.pluginManager.loadClass(rt.FBXIMP);rt.FBXImporterSetParam('PushSettings')
        try:
            for k,v in {'Mode':rt.Name(mode),'Animation':True,'TakeIndex':1,'FillTimeline':True,'GenerateLog':False,'FilterKeyReducer':False,'PostSamplingKeyReducer':False,'KeepFrameRate':True}.items():rt.FBXImporterSetParam(k,v)
            if not rt.importFile(str(path),rt.Name('noPrompt'),using=rt.FBXIMP):raise RuntimeError('FBX import failed: '+str(path))
        finally:rt.FBXImporterSetParam('PopSettings')
        rt.frameRate=rate
    def ticks(seconds):return int(round(seconds*rate*int(rt.ticksPerFrame)))
    def sample(path,samples=end+1,offset=0,root=None):
        checked(rt.YamoArmChainSample(root or find(root_name),str(path),0,samples-1,int(offset)),samples,'Arm sample')
        return Poses.load(path)
    def create_cache(path,filename,source_names=None,samples=end+1,seconds=0):
        reset();import_fbx(path,'create');root=None
        if source_names:
            raw_nodes=[find(n) for n in source_names]
            if len(set(str(n.name) for n in raw_nodes))!=8:raise RuntimeError('Raw arm nodes are not unique')
            root=rt.Point(name='YamoRawArmCache')
            for n,suffix in zip(raw_nodes,SUFFIXES):n.name='YamoRawArmCache'+suffix
        return sample(work/filename,samples,ticks(seconds),root)
    progress('Caching original raw hand poses')
    raw=create_cache(job['rawFbx'],'raw.bin',job['rawBoneNames'],seconds=job['rawStartSeconds'])
    if 'rawDurationSeconds' in job and abs(end/rate-job['rawDurationSeconds'])>1e-7:
        last=sample(work/'raw-last.bin',1,ticks(job['rawStartSeconds']+job['rawDurationSeconds']),find('YamoRawArmCache'))
        raw.data[-56:]=last.data
    raw_ref=sample(work/'raw-reference.bin',1,ticks(job['rawReferenceSeconds']),find('YamoRawArmCache'))
    target_ref=create_cache(job['referenceFbx'],'target-reference.bin',samples=1)
    progress('Importing body motion into a fresh Base MAX')
    if not rt.loadMaxFile(job['baseMax'],useFileUnits=True,quiet=True):raise RuntimeError('Cannot reload Base MAX')
    rt.frameRate=rate;root=find(root_name);root.controller.figureMode=False
    if root.controller.sepArmsTracks:raise RuntimeError('Base MAX requires Separate Arms Tracks off')
    object_count=int(rt.objects.count);import_fbx(job['bodyFbx'],'exmerge')
    if int(rt.objects.count)!=object_count:raise RuntimeError('FBX merge created unexpected objects')
    first=1
    rt.animationRange=rt.interval(rt.execute('1f'),rt.execute(str(end+first)+'f'))
    reference_zero=sample(work/'native-reference-zero.bin',1)
    baseline=sample(work/'baseline-native.bin',offset=int(rt.ticksPerFrame)*first)
    protected=[n for n in rt.objects if str(rt.classOf(n))=='Biped_Object' and str(n.name) not in [root_name+SUFFIXES[i] for i in (1,2,3,5,6,7)] and 'Footsteps' not in str(n.name)]
    protected_names=[str(n.name) for n in protected]
    protection_frames=sorted(set([first,first+1,first+end//4,first+end//2,first+3*end//4,first+end]))
    def protected_poses():
        rows=[];protected=[find(n) for n in protected_names]
        for f in protection_frames:
            with pymxs.attime(rt.execute(str(f)+'f')):
                row=[]
                for n in protected:
                    m=n.transform
                    if n.parent is not None:m=m*rt.inverse(n.parent.transform)
                    p=m.row4;q=rt.quat(m);row.append(((float(p.x),float(p.y),float(p.z)),(float(q.x),float(q.y),float(q.z),float(q.w))))
                rows.append(row)
        return rows
    before=protected_poses()
    targets,scales=make_targets(raw,baseline,raw_ref,target_ref);targets.save(work/'hand-targets.bin')
    stable,poles=stabilize_poles(targets);stable.save(work/'stable-targets.bin');atomic_json(work/'pole-stabilization.json',poles)
    progress('Writing native Biped arm keys')
    from arm_math import qangle
    preserve_reference_zero=abs(job['rawStartSeconds']-job['rawReferenceSeconds'])<1e-7 and all(
        length(sub(targets.pos(0,i),baseline.pos(0,i)))/units_per_cm<.005 and
        qangle(targets.quat(0,i),baseline.quat(0,i))<.005 for i in (3,7))
    native_targets=Poses(stable.frames+1,reference_zero.data+stable.data)
    native_targets.save(work/'native-targets-with-reference.bin')
    checked(rt.YamoArmChainApply(root,str(work/'native-targets-with-reference.bin'),2 if preserve_reference_zero else 1),end+1,'Native key bake')
    # Source/base files are never saved; only a new scene under the job directory is written.
    max_path=work/('Ani_'+job['name']+'.max')
    for tex in rt.getClassInstances(rt.Bitmaptexture):
        if str(tex.filename):
            absolute=str(rt.pathConfig.convertPathToAbsolute(str(tex.filename)))
            if os.path.isfile(absolute):tex.filename=absolute
    rt.execute('sliderTime = 1f')
    if not rt.saveMaxFile(str(max_path),quiet=True) or not rt.loadMaxFile(str(max_path),useFileUnits=True,quiet=True):raise RuntimeError('MAX save/reload failed')
    if int(rt.objects.count)!=object_count:raise RuntimeError('MAX reload changed object count')
    rt.frameRate=rate
    native=sample(work/'saved-native.bin',offset=int(rt.ticksPerFrame)*first);hand_quality=compare(targets,native,units_per_cm)
    if hand_quality['handPositionCm']['max']>.05 or hand_quality['handRotationDeg']['max']>.1:raise RuntimeError('Saved MAX missed raw hand goals: '+json.dumps(hand_quality))
    plane_errors=[]
    for f in range(end+1):
        for b in (0,4):
            def plane(p):
                s=p.pos(f,b+1);e=p.pos(f,b+2);h=p.pos(f,b+3);axis=unit(sub(h,s));v=sub(e,s)
                return sub(v,mul(axis,dot(v,axis))),angle(sub(e,s),sub(h,e))
            a,fa=plane(stable);bplane,fb=plane(native)
            if min(fa,fb)>10:plane_errors.append(angle(a,bplane))
    if max(plane_errors or [0])>2:raise RuntimeError('Native elbow plane differs from the solved plane')
    after=protected_poses();protection_error=0
    for a,b in zip(before,after):
        for (p,q),(P,Q) in zip(a,b):protection_error=max(protection_error,length(sub(p,P)),1-abs(dot(unit(q),unit(Q))))
    if protection_error>.003:raise RuntimeError('Unrelated Biped local poses changed')
    selection_name=job['selectionSet']
    literal='"'+selection_name.replace('\\','\\\\').replace('"','\\"')+'"'
    selection=rt.execute('for n in selectionSets['+literal+'] collect n')
    if len(selection)<8 or len(selection)>2048:raise RuntimeError('Invalid export selection set')
    selection_names=[str(n.name) for n in selection]
    if not set(names+[root_name]).issubset(selection_names):raise RuntimeError('Export selection set must contain COM and all eight arm bones')
    if any('Footsteps' in str(n.name) for n in selection):raise RuntimeError('Export selection set must exclude Footsteps')
    rt.select(list(selection));cache=work/'export-skeleton.bin'
    checked(rt.YamoArmChainCapture(str(cache),first,end+first),end+1,'Skeleton capture')
    times=set()
    for f in sorted(set(range(4,end,180))|{4,end//4,end//2,3*end//4,end-1}):
        for d in (-.5,-.25,0,.25,.5):
            if 0<=f+d<=end:times.add(f+d)
    for span in poles['windows']:
        for k in range(span['first']*4,span['last']*4+1):times.add(k/4)
    times=sorted(t for t in times if t>=3) or [float(end)]
    def fractional(offset=0):
        nodes=[find(n) for n in names];poses=Poses(len(times))
        for f,t in enumerate(times):
            with pymxs.attime(rt.execute(str(int(round((t+offset)*int(rt.ticksPerFrame))))+'t')):
                for i,n in enumerate(nodes):
                    m=n.transform;p=m.row4;q=rt.quat(m)
                    poses.set_pos(f,i,(p.x,p.y,p.z));poses.set_quat(f,i,(q.x,q.y,q.z,q.w))
        return poses
    native_fraction=fractional(first);native_fraction.save(work/'native-fractional.bin')
    max_sha=sha(max_path);candidates=[]
    for order,label in [(0,'XYZ'),(5,'ZYX')]:
        progress('Exporting explicit per-frame FK '+label)
        reset();checked(rt.YamoArmChainBuild(str(cache),True,order),len(selection_names),'FK skeleton construction')
        rt.select(list(rt.objects));rt.animationRange=rt.interval(rt.execute('0f'),rt.execute(str(end)+'f'))
        path=work/('export-'+label+'.fbx');rt.pluginManager.loadClass(rt.FBXEXP);rt.FBXExporterSetParam('PushSettings')
        try:
            rt.FBXExporterSetParam('ResetExport')
            for k,v in {'Animation':True,'BakeAnimation':False,'BakeFrameStart':0,'BakeFrameEnd':end,'BakeFrameStep':1,'BakeResampleAnimation':False,'FilterKeyReducer':False,'RemoveSingleKeys':False,'ASCII':False,'UpAxis':'Y','UseSceneName':False,'GenerateLog':False,'ShowWarnings':False,'Cameras':False,'Lights':False,'EmbedTextures':False}.items():rt.FBXExporterSetParam(k,v)
            if not rt.exportFile(str(path),rt.Name('noPrompt'),selectedOnly=True,using=rt.FBXEXP):raise RuntimeError('FK FBX export failed')
        finally:rt.FBXExporterSetParam('PopSettings')
        from fbx_timing import inspect
        timing=inspect(path,rate)
        exported=create_cache(str(path),'export-'+label+'.bin');quality=compare(native,exported,units_per_cm)
        if quality['allArmPositionCm']['max']>.01 or quality['allArmRotationDeg']['max']>.02:raise RuntimeError('FBX recorded-frame arm chain validation failed')
        fractions=fractional();fractions.save(work/('fractional-'+label+'.bin'));subframes=compare(native_fraction,fractions,units_per_cm)
        candidates.append(dict(order=label,fbx=str(path),recorded=quality,subframes=subframes,timing=timing))
    a,b=candidates;A=a['subframes']['handRotationDeg'];B=b['subframes']['handRotationDeg']
    chosen=b if B['max']<=A['max'] and B['p95']<=A['p95']*1.05 else a
    if sha(job['rawFbx'])!=source_hash or sha(job['baseMax'])!=base_hash or sha(max_path)!=max_sha:raise RuntimeError('Source, Base MAX or saved MAX changed during export')
    result=dict(schemaVersion=1,token=job['token'],name=job['name'],passed=True,max=str(max_path),maxSha256=max_sha,fbx=chosen['fbx'],fbxSha256=sha(chosen['fbx']),frames=end+1,fps=rate,rawUnchanged=True,baseUnchanged=True,armScale=scales,upperArmTwist=0.5,lowerArmTwist=0,forearmHingeCorrection=False,rawHandToSavedMax=hand_quality,elbowPlaneErrorDeg=stats(plane_errors),protectedLocalMaxError=protection_error,handEulerOrder=chosen['order'],candidates=candidates,poleStabilization=poles,exportBoneCount=len(selection_names),subframeSamples=len(times),armKeysUseNativeBipedControllers=True,maxStartFrame=first,maxEndFrame=end+first,fbxStartFrame=0,fbxEndFrame=end,preservedFirstSourceTpose=preserve_reference_zero,systemUnits=str(units),unitScale=unit_scale,unitsPerCentimeter=units_per_cm)
    atomic_json(work/'result.json',result);progress('Verified and ready to publish')


if __name__=='__main__':
    job=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
    try:run(job)
    except:
        atomic_json(Path(job['workDirectory'])/'failure.json',dict(passed=False,token=job['token'],error=traceback.format_exc()))
        raise
