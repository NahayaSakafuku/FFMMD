"""Headless MMD Tools physics bake helper for FFMMD.

This script only calls Blender and the installed GPL MMD Tools addon through its public
operators. It does not copy or embed addon code. The generated cache is consumed by a
separate plugin reader and contains relative deltas for selected skirt bones.

Usage (PowerShell):
  E:\blender4\blender.exe -b --factory-startup --python BlenderBakeSkirt.py -- `
    --pmx model.pmx --vmd motion.vmd --out skirt-cache.json
"""
import argparse, hashlib, json, math, os, struct, sys, tomllib
from pathlib import Path
import bpy
if str(Path(__file__).parent) not in sys.path:
    sys.path.insert(0, str(Path(__file__).parent))
import skirt_smoothing as smoothing
# Blender extensions are not necessarily on sys.path in --factory-startup.
addon_root = os.path.expandvars(r"%APPDATA%\Blender Foundation\Blender\4.2\extensions\user_default")
if addon_root not in sys.path:
    sys.path.insert(0, addon_root)
from mathutils import Quaternion, Vector

def progress(phase,fraction):
    print('FFMMD_PROGRESS '+json.dumps({'Phase':phase,'Fraction':max(0,min(1,fraction))},separators=(',',':')),flush=True)

def quaternion_tuple(q): return (q.x,q.y,q.z,q.w)

def rigid_geometry(obj,evaluated_arm):
    """Object geometry in the same armature coordinates used by output deltas."""
    evaluated=obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    transform=evaluated_arm.matrix_world.inverted() @ evaluated.matrix_world
    low=Vector(tuple(min(v[i] for v in obj.bound_box) for i in range(3)))
    high=Vector(tuple(max(v[i] for v in obj.bound_box) for i in range(3)))
    center=transform @ ((low+high)/2)
    stretch=Vector(tuple(transform.to_3x3().col[i].length for i in range(3)))
    half=(high-low)/2
    half=tuple(half[i]*stretch[i] for i in range(3))
    rotation=quaternion_tuple(matrix_rot(transform))
    shape=obj.mmd_rigid.shape
    if shape=='BOX': return (tuple(center),rotation,half)
    if shape not in ('SPHERE','CAPSULE'): raise RuntimeError('Unsupported reference collider shape '+shape)
    radius=max(half[0],half[1])
    axis=smoothing.rotate(rotation,(0,0,1))
    height=max(0,half[2]-radius) if shape=='CAPSULE' else 0
    return (smoothing.sub(tuple(center),smoothing.scale(axis,height)),
            smoothing.add(tuple(center),smoothing.scale(axis,height)),radius)

def contact_measure(rotations,observation,rest_quaternions,parent_indices):
    deltas=[];heads=[];model_rotations=[];boxes=[]
    for i,relative in enumerate(rotations):
        parent=parent_indices[i]
        parent_delta=deltas[parent] if parent>=0 else observation['ParentDelta'][i]
        delta=smoothing.multiply(parent_delta,relative)
        q=smoothing.normalize(smoothing.multiply(delta,rest_quaternions[i]))
        raw_q=observation['BoneRotations'][i]
        correction=smoothing.multiply(q,smoothing.conjugate(raw_q))
        head=observation['Heads'][i]
        if parent>=0:
            parent_correction=smoothing.multiply(model_rotations[parent],smoothing.conjugate(observation['BoneRotations'][parent]))
            head=smoothing.add(heads[parent],smoothing.rotate(parent_correction,smoothing.sub(head,observation['Heads'][parent])))
        raw_box=observation['Boxes'][i]
        center=smoothing.add(head,smoothing.rotate(correction,smoothing.sub(raw_box[0],observation['Heads'][i])))
        boxes.append((center,smoothing.normalize(smoothing.multiply(correction,raw_box[1])),raw_box[2]))
        deltas.append(delta);heads.append(head);model_rotations.append(q)
    return [smoothing.capsule_box_penetration(capsule,box) for box in boxes for capsule in observation['Colliders']]

def smooth_frames(frames,observations,rest_quaternions,parent_indices,settings):
    raw_tracks=[[frame['Rotations'][i] for frame in frames] for i in range(len(frames[0]['Rotations']))]
    candidates=[smoothing.smooth_track(track,settings.smooth_window,settings.smooth_strength,settings.smooth_max_degrees) for track in raw_tracks]
    result=[];accepted=0;backoff=0;rejected=0;maximum_correction=0;maximum_increase=0
    raw_max=0;final_max=0;raw_contacts=0;final_contacts=0;alphas=[]
    for i,(frame,observation) in enumerate(zip(frames,observations)):
        raw=frame['Rotations'];candidate=[track[i] for track in candidates]
        measure=lambda values:contact_measure(values,observation,rest_quaternions,parent_indices)
        filtered,amount,before,after=smoothing.guarded_frame(raw,candidate,measure,tolerance=1e-7)
        accepted+=amount==1;backoff+=0<amount<1;rejected+=amount==0;alphas.append(amount)
        maximum_correction=max(maximum_correction,max(smoothing.angular_distance(a,b) for a,b in zip(raw,filtered)))
        maximum_increase=max(maximum_increase,max((b-a for a,b in zip(before,after)),default=0))
        raw_max=max(raw_max,max(before,default=0));final_max=max(final_max,max(after,default=0))
        raw_contacts+=sum(v>1e-7 for v in before);final_contacts+=sum(v>1e-7 for v in after)
        result.append({'Frame':frame['Frame'],'Rotations':filtered})
        if i%100==0:progress('smooth',i/max(1,len(frames)-1))
    filtered_tracks=[[frame['Rotations'][i] for frame in result] for i in range(len(raw_tracks))]
    metrics={'Algorithm':'SymmetricBilateralQuaternion','WindowFrames':settings.smooth_window,
        'MaximumCorrectionDegrees':settings.smooth_max_degrees,'Strength':settings.smooth_strength,
        'AngularBandwidthDegrees':12,'ContactGuard':'ReferenceSkirtBoxesVsLegCapsules',
        'ContactGuardTolerance':1e-7,'MaximumBackoffs':8,'FrameBlend':'CommonAcrossAllPanels',
        'FullyAcceptedFrames':accepted,'ReducedBlendFrames':backoff,'OriginalFallbackFrames':rejected,
        'MeanAcceptedBlend':sum(alphas)/max(1,len(alphas)),
        'MeasuredMaximumCorrectionDegrees':math.degrees(maximum_correction),
        'MaximumProxyPenetrationIncrease':maximum_increase,'OriginalMaximumProxyPenetration':raw_max,
        'FilteredMaximumProxyPenetration':final_max,'OriginalProxyContactPairs':raw_contacts,
        'FilteredProxyContactPairs':final_contacts,'OriginalMotion':smoothing.track_metrics(raw_tracks),
        'FilteredMotion':smoothing.track_metrics(filtered_tracks),
        'Scope':'Reference rigid-body proxies only; target skirt mesh penetration is not measured'}
    progress('smooth',1)
    return result,metrics

def sha256(path):
    h=hashlib.sha256()
    with open(path,'rb') as f:
        for chunk in iter(lambda:f.read(1024*1024),b''): h.update(chunk)
    return h.hexdigest()

def arg_parser():
    p=argparse.ArgumentParser()
    p.add_argument('--pmx',required=True); p.add_argument('--vmd',required=True); p.add_argument('--out',required=True)
    p.add_argument('--scale',type=float,default=0.08); p.add_argument('--fps',type=float,default=30.0)
    p.add_argument('--substeps',type=int,default=120); p.add_argument('--iterations',type=int,default=40)
    p.add_argument('--warmup',type=int,default=60); p.add_argument('--start',type=int,default=0); p.add_argument('--end',type=int,default=-1)
    p.add_argument('--collision-margin',type=float,default=1e-6); p.add_argument('--non-collision-scale',type=float,default=1.5)
    p.add_argument('--smooth-window',type=int,default=2);p.add_argument('--smooth-max-degrees',type=float,default=3)
    p.add_argument('--smooth-strength',type=float,default=.65)
    p.add_argument('--skirt-angular-damping',type=float,default=-1,help='diagnostic override; -1 keeps PMX values')
    p.add_argument('--no-physics',action='store_true',help='export a body/VMD baseline without importing or solving rigid bodies')
    p.add_argument('--bones',default='',help='comma-separated source names; default all names matching Skirt_/裙')
    return p

def armature_from_root(root):
    for ob in bpy.data.objects:
        if ob.type=='ARMATURE' and (ob.parent==root or ob.name.startswith(root.name)):
            return ob
    for ob in bpy.data.objects:
        if ob.type=='ARMATURE': return ob
    raise RuntimeError('MMD armature not found')

def matrix_rot(m): return m.to_3x3().normalized().to_quaternion().normalized()
def relative_delta(arm,bone,rest_rotations):
    # PoseBone.matrix is in armature coordinates. This is a reference-axis delta,
    # not Blender's bone-axis matrix_basis or rotation_quaternion.
    d=matrix_rot(bone.matrix) @ rest_rotations[bone.name].conjugated()
    parent=bone.parent
    if parent:
        pd=matrix_rot(parent.matrix) @ rest_rotations[parent.name].conjugated()
        d=pd.conjugated() @ d
    return d.normalized()
def basis(arm):
    pos={b.name:b.bone.head_local.copy() for b in arm.pose.bones}
    def find(*names):
        for n in names:
            if n in pos:return pos[n]
    left=find('左足','左脚','j_asi_a_l'); right=find('右足','右脚','j_asi_a_r')
    head=find('頭','head'); pelvis=find('下半身','pelvis','j_kosi')
    ankles=[find('左足首'),find('右足首')]; toes=[find('左足先EX','左つま先ＩＫ'),find('右足先EX','右つま先ＩＫ')]
    if any(x is None for x in [left,right,head,pelvis,*ankles,*toes]):
        raise RuntimeError('Cannot derive the reference rig Left/Up/Back basis')
    up=(head-pelvis).normalized()
    leftv=left-right; leftv=(leftv-up*leftv.dot(up)).normalized()
    back=(sum(ankles,Vector())-sum(toes,Vector()))/2
    back=(back-up*back.dot(up)-leftv*back.dot(leftv)).normalized()
    if min(up.length,leftv.length,back.length)<.99: raise RuntimeError('Degenerate reference basis')
    return {'Left':[leftv.x,leftv.y,leftv.z], 'Up':[up.x,up.y,up.z], 'Back':[back.x,back.y,back.z]}
def motion_max_frame(path):
    data=Path(path).read_bytes(); header=data[:30].split(b'\0')[0]
    name_length=20 if header==b'Vocaloid Motion Data 0002' else 10 if header==b'Vocaloid Motion Data file' else 0
    if not name_length or len(data)<34+name_length: raise RuntimeError('Unsupported VMD header')
    offset=30+name_length; count=struct.unpack_from('<I',data,offset)[0]; offset+=4
    if count>10000000 or offset+count*111>len(data): raise RuntimeError('Truncated VMD bone records')
    maximum=max((struct.unpack_from('<I',data,offset+i*111+15)[0] for i in range(count)),default=0)
    if maximum>1000000: raise RuntimeError('VMD duration exceeds helper limit')
    return maximum
def target_name(source):
    parts=source.split('_')
    if len(parts)!=3 or parts[0]!='Skirt': raise RuntimeError('Unsupported skirt source name: '+source)
    layer,column=int(parts[1]),int(parts[2]); directions=['f_r','f_l','s_l','b_l','b_r','s_r']
    if layer not in range(3) or column not in range(6): raise RuntimeError('Unsupported skirt source name: '+source)
    return 'j_sk_'+directions[column].split('_')[0]+'_'+['a','b','c'][layer]+'_'+directions[column].split('_')[1]
def main():
    ap=arg_parser(); ns=ap.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    if ns.fps!=30 or not math.isfinite(ns.scale) or not 0.001<=ns.scale<=100:
        raise ValueError('The cache requires 30 fps and a finite positive PMX/VMD scale')
    if not 1<=ns.substeps<=1000 or not 1<=ns.iterations<=1000 or not 0<=ns.warmup<=300:
        raise ValueError('Invalid solver substeps, iterations, or warmup')
    if ns.start<0 or ns.end!=-1 and ns.end<ns.start:
        raise ValueError('Invalid bake frame range')
    if not 0<=ns.collision_margin<=1 or not 0<=ns.non_collision_scale<=100:
        raise ValueError('Invalid collision margin or collision distance scale')
    if not 0<=ns.smooth_window<=8 or not 0<=ns.smooth_max_degrees<=15 or not 0<=ns.smooth_strength<=1:
        raise ValueError('Invalid smoothing settings')
    if ns.skirt_angular_damping!=-1 and not 0<=ns.skirt_angular_damping<=1:
        raise ValueError('Invalid skirt angular damping override')
    pmx=os.path.abspath(ns.pmx); vmd=os.path.abspath(ns.vmd); out=os.path.abspath(ns.out)
    if not os.path.isfile(pmx) or not os.path.isfile(vmd): raise FileNotFoundError('PMX/VMD missing')
    if os.path.normcase(out) in {os.path.normcase(pmx),os.path.normcase(vmd)}:
        raise ValueError('Output must be separate from the PMX and VMD input files')
    pmx_fingerprint=sha256(pmx); motion_fingerprint=sha256(vmd)
    progress('import',0)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    import mmd_tools
    mmd_tools.register()
    import_types={'ARMATURE'} if ns.no_physics else {'ARMATURE','PHYSICS'}
    bpy.ops.mmd_tools.import_model(filepath=pmx, types=import_types, scale=ns.scale, rename_bones=False, fix_IK_links=False)
    root=next((o for o in bpy.data.objects if getattr(o,'mmd_type',None)=='MODEL'),None)
    if root is None: root=next((o for o in bpy.data.objects if o.type=='EMPTY'),None)
    arm=armature_from_root(root)
    bpy.ops.object.select_all(action='DESELECT'); bpy.context.view_layer.objects.active=arm; arm.select_set(True)
    # 4.2.2 processes files/directory rather than filepath in this operator.
    bpy.ops.mmd_tools.import_vmd(filepath=vmd, files=[{'name':os.path.basename(vmd)}], directory=os.path.dirname(vmd)+os.sep, scale=ns.scale, bone_mapper='PMX', rename_bones=False, margin=0, update_scene_settings=True, use_NLA=False)
    action=arm.animation_data.action if arm.animation_data else None
    if action is None or not action.fcurves: raise RuntimeError('VMD import produced no body animation')
    # Hold the first animation pose for a fixed pre-roll rather than blending from
    # the PMX rest pose. Original VMD frame zero is now Blender warmup+1.
    for action in bpy.data.actions:
        for fc in action.fcurves:
            for kp in fc.keyframe_points:
                kp.co.x+=ns.warmup; kp.handle_left.x+=ns.warmup; kp.handle_right.x+=ns.warmup
    scene=bpy.context.scene; maximum=motion_max_frame(vmd)
    start=ns.start; end=min(ns.end,maximum) if ns.end>=start else maximum
    if start>maximum: raise ValueError('Bake starts past the VMD end')
    scene.frame_start=1; scene.frame_end=end+ns.warmup+1
    scene.render.fps=30; scene.render.fps_base=1.0; scene.frame_set(1)
    source_basis=basis(arm); rest_rotations={b.name:matrix_rot(b.matrix_local) for b in arm.data.bones}
    world=None; point_cache=None
    if not ns.no_physics:
        bpy.context.view_layer.objects.active=root; root.select_set(True)
        bpy.ops.mmd_tools.rigid_body_world_update()
        bpy.ops.mmd_tools.build_rig(non_collision_distance_scale=ns.non_collision_scale, collision_margin=ns.collision_margin)
        world=bpy.context.scene.rigidbody_world
        if world is None: raise RuntimeError('rigid body world unavailable')
        world.enabled=True; world.substeps_per_frame=ns.substeps; world.solver_iterations=ns.iterations
    scene.render.fps=int(ns.fps); scene.render.fps_base=1.0
    if world is not None:
        point_cache=world.point_cache
        if point_cache is None: raise RuntimeError('Rigid body point cache unavailable')
        point_cache.frame_start=1; point_cache.frame_end=scene.frame_end
    names=[x for x in (ns.bones.split(',') if ns.bones else []) if x]
    if not names: names=['Skirt_'+str(layer)+'_'+str(column) for layer in range(3) for column in range(6)]
    missing=[n for n in names if n not in arm.pose.bones]
    if missing: raise RuntimeError('Missing skirt bones: '+','.join(missing))
    bones=[arm.pose.bones[n] for n in names]
    if len(names)!=len(set(names)): raise RuntimeError('Duplicate skirt bones')
    rigid_by_bone={o.mmd_rigid.bone:o for o in bpy.data.objects if o.rigid_body is not None and getattr(o,'mmd_type','')=='RIGID_BODY'}
    # These PMX body colliders are retained. No skirt or horizontal joint is removed.
    leg_names={'左足','右足','左ひざ','右ひざ'}
    colliders=[o for o in bpy.data.objects if o.rigid_body is not None and getattr(o,'mmd_type','')=='RIGID_BODY'
               and o.mmd_rigid.bone in leg_names and o.mmd_rigid.shape in ('SPHERE','CAPSULE')]
    skirt_rigids=[rigid_by_bone.get(n) for n in names]
    guard_enabled=not ns.no_physics and ns.smooth_window>0 and ns.smooth_strength>0
    if guard_enabled and (len(colliders)<4 or any(o is None or o.mmd_rigid.shape!='BOX' for o in skirt_rigids)):
        raise RuntimeError('Reference rig lacks the required skirt box / leg sphere-capsule contact geometry')
    damping=[{'SourceName':name,'LinearDamping':obj.rigid_body.linear_damping,
              'AngularDamping':obj.rigid_body.angular_damping,'Mass':obj.rigid_body.mass}
             for name,obj in zip(names,skirt_rigids) if obj is not None]
    if ns.skirt_angular_damping!=-1:
        for obj in skirt_rigids:
            if obj is not None:obj.rigid_body.angular_damping=ns.skirt_angular_damping
    parent_indices=[names.index(b.parent.name) if b.parent is not None and b.parent.name in names else -1 for b in bones]
    if any(parent>=i for i,parent in enumerate(parent_indices)):
        raise RuntimeError('Skirt bones must be supplied in parent-before-child order')
    rest_quaternions=[quaternion_tuple(rest_rotations[n]) for n in names]
    progress('import',1)
    if point_cache is not None:
        progress('bake',0)
        print('FFMMD_BAKE_START',scene.frame_end,'Blender frames',flush=True)
        def bake_progress(scene,*unused):
            if scene.frame_current%120==0:progress('bake',scene.frame_current/max(1,scene.frame_end))
        bpy.app.handlers.frame_change_post.append(bake_progress)
        try:
            with bpy.context.temp_override(scene=scene,point_cache=point_cache):
                bake_result=bpy.ops.ptcache.bake('EXEC_DEFAULT',bake=True)
        finally:
            bpy.app.handlers.frame_change_post.remove(bake_progress)
        if 'FINISHED' not in bake_result or not point_cache.is_baked:
            raise RuntimeError('Blender point cache did not complete')
        progress('bake',1)
    frames=[]
    observations=[]
    previous={}
    for fr in range(start,end+1):
        scene.frame_set(fr+ns.warmup+1); bpy.context.view_layer.update()
        evaluated_arm=arm.evaluated_get(bpy.context.evaluated_depsgraph_get())
        rotations=[]
        for b in bones:
            q=relative_delta(evaluated_arm,evaluated_arm.pose.bones[b.name],rest_rotations)
            if b.name in previous and q.dot(previous[b.name])<0:
                q.negate()
            previous[b.name]=q.copy()
            rotations.append([q.x,q.y,q.z,q.w])
        frames.append({'Frame':fr,'Rotations':rotations})
        if guard_enabled:
            parent_delta=[]
            for bone in bones:
                parent=evaluated_arm.pose.bones.get(bone.parent.name) if bone.parent else None
                parent_delta.append(quaternion_tuple(matrix_rot(parent.matrix) @ rest_rotations[parent.name].conjugated()) if parent else (0,0,0,1))
            observations.append({'Heads':[tuple(evaluated_arm.pose.bones[n].head) for n in names],
                'BoneRotations':[quaternion_tuple(matrix_rot(evaluated_arm.pose.bones[n].matrix)) for n in names],
                'ParentDelta':parent_delta,'Boxes':[rigid_geometry(o,evaluated_arm) for o in skirt_rigids],
                'Colliders':[rigid_geometry(o,evaluated_arm) for o in colliders]})
        if (fr-start)%100==0:progress('export',(fr-start)/max(1,end-start))
        if point_cache is not None and (fr-start)%500==0: print('FFMMD_BAKE_EXPORT',fr,flush=True)
    progress('export',1)
    smoothing_metrics={'Algorithm':'Disabled','Scope':'No filtering was applied'}
    if guard_enabled:
        frames,smoothing_metrics=smooth_frames(frames,observations,rest_quaternions,parent_indices,ns)
    manifest=tomllib.loads(Path(mmd_tools.__file__).with_name('blender_manifest.toml').read_text(encoding='utf-8'))
    if pmx_fingerprint!=sha256(pmx) or motion_fingerprint!=sha256(vmd):
        raise RuntimeError('A source file changed during the bake; output was not replaced')
    result={'SchemaVersion':1,'MotionSha256':motion_fingerprint,'ReferencePmxSha256':pmx_fingerprint,'Fps':ns.fps,'StartFrame':start,'FrameCount':len(frames),'SourceBasis':source_basis,'Bones':[{'SourceName':b.name,'TargetName':target_name(b.name),'ParentSourceName':b.bone.parent.name if b.bone.parent else '','RelativeRotations':[]} for b in bones],'Solver':{'RecipeVersion':2,'Engine':'Blender baseline (rigid bodies disabled)' if ns.no_physics else 'Blender Rigid Body Bullet','BlenderVersion':bpy.app.version_string,'AddonVersion':manifest['version'],'Substeps':0 if ns.no_physics else ns.substeps,'Iterations':0 if ns.no_physics else ns.iterations,'WarmupFrames':ns.warmup,'Scale':ns.scale,'MotionMaxFrame':maximum,'CollisionMargin':0 if ns.no_physics else ns.collision_margin,'NonCollisionDistanceScale':0 if ns.no_physics else ns.non_collision_scale,'PointCacheBaked':False if point_cache is None else point_cache.is_baked,'RigidBodyCount':sum(o.rigid_body is not None for o in bpy.data.objects),'JointCount':sum(getattr(o,'mmd_type','')=='JOINT' for o in bpy.data.objects),'Smoothing':smoothing_metrics,'OriginalSkirtRigidParameters':damping,'AngularDampingOverride':ns.skirt_angular_damping}}
    for i in range(len(bones)): result['Bones'][i]['RelativeRotations']=[fr['Rotations'][i] for fr in frames]
    Path(out).parent.mkdir(parents=True,exist_ok=True)
    temporary=Path(out+'.tmp')
    progress('write',0)
    temporary.write_text(json.dumps(result,ensure_ascii=False,separators=(',',':'),allow_nan=False),encoding='utf-8')
    os.replace(temporary,out)
    progress('write',1)
    print('FFMMD_BAKE_OK',out,'frames=',len(frames),'bones=',len(bones))
if __name__=='__main__': main()
