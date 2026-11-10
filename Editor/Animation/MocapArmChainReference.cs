using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Fbx;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// Keep Max's reference key at 0f; the selected motion begins at 1f.
    /// Final FK export removes this preroll and begins at 0f again.
    /// </summary>
    internal static class MocapArmChainReference
    {
        internal static void Create(string body, string reference, string output, int rate)
        {
            using (var manager = FbxManager.Create())
            {
                var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(io);
                using (var scene = FbxScene.Create(manager, "Arm chain body with reference"))
                using (var refScene = FbxScene.Create(manager, "Arm chain reference"))
                {
                    Read(manager, io, body, scene); Read(manager, io, reference, refScene);
                    var stack = scene.GetCurrentAnimationStack();
                    if (stack == null || stack.GetMemberCount() != 1)
                        throw new InvalidDataException("Body FBX requires one animation stack/layer.");
                    var layer = stack.GetAnimLayerMember(0);
                    var refStack = refScene.GetCurrentAnimationStack();
                    var refLayer = refStack == null ? null : refStack.GetAnimLayerMember(0);
                    var nodes = new Dictionary<string, FbxNode>(StringComparer.Ordinal);
                    Action<FbxNode> collect = null;
                    collect = n => { if (nodes.ContainsKey(n.GetName())) throw new InvalidDataException("Duplicate reference node name");
                        nodes.Add(n.GetName(), n); for (int i=0;i<n.GetChildCount();i++) collect(n.GetChild(i)); };
                    for (int i=0;i<refScene.GetRootNode().GetChildCount();i++) collect(refScene.GetRootNode().GetChild(i));
                    var offset = 1.0 / rate;
                    using (var oldSpan = stack.GetLocalTimeSpan())
                    using (var start = FbxTime.FromSecondDouble(0))
                    using (var stop = FbxTime.FromSecondDouble(oldSpan.GetStop().GetSecondDouble() + offset))
                    using (var span = new FbxTimeSpan(start, stop))
                        stack.SetLocalTimeSpan(span);
                    Action<FbxNode> shift = null;
                    shift = node =>
                    {
                        FbxNode pose;
                        if (!nodes.TryGetValue(node.GetName(), out pose))
                        {
                            if (node.GetParent() == scene.GetRootNode() && refScene.GetRootNode().GetChildCount() == 1)
                                pose = refScene.GetRootNode().GetChild(0);
                            else throw new InvalidDataException("Reference node missing: " + node.GetName());
                        }
                        ShiftProperty(node.LclTranslation, pose.LclTranslation, layer, refLayer, offset);
                        ShiftProperty(node.LclRotation, pose.LclRotation, layer, refLayer, offset);
                        ShiftProperty(node.LclScaling, pose.LclScaling, layer, refLayer, offset);
                        for (int i=0;i<node.GetChildCount();i++) shift(node.GetChild(i));
                    };
                    for(int i=0;i<scene.GetRootNode().GetChildCount();i++) shift(scene.GetRootNode().GetChild(i));
                    using(var writer=FbxExporter.Create(manager,"Write body reference"))
                        if(!writer.Initialize(output,-1,io)||!writer.Export(scene))throw new IOException("Reference FBX write failed");
                }
            }
        }

        private static void Read(FbxManager manager,FbxIOSettings io,string path,FbxScene scene)
        {
            using(var reader=FbxImporter.Create(manager,"Read reference input"))
                if(!reader.Initialize(path,-1,io)||!reader.Import(scene))throw new IOException("Cannot read FBX: "+path);
        }

        private static void ShiftProperty(FbxPropertyDouble3 property,FbxPropertyDouble3 reference,
            FbxAnimLayer layer,FbxAnimLayer referenceLayer,double offset)
        {
            var before=property.Get();var pose=reference.Get();var values=new double[3];
            for(int axis=0;axis<3;axis++)
            {
                var channel=axis==0?"X":axis==1?"Y":"Z";
                var refCurve=referenceLayer==null?null:reference.GetCurve(referenceLayer,channel,false);
                values[axis]=refCurve!=null&&refCurve.KeyGetCount()>0?refCurve.KeyGetValue(0):pose[axis];
                var curve=property.GetCurve(layer,channel,true);
                curve.KeyModifyBegin();
                try
                {
                    var count=curve.KeyGetCount();
                    for(int i=count-1;i>=0;i--)
                        using(var oldTime=curve.KeyGetTime(i))
                        using(var newTime=FbxTime.FromSecondDouble(oldTime.GetSecondDouble()+offset))
                            curve.KeySetTime(i,newTime);
                    if(count==0)
                        using(var motionStart=FbxTime.FromSecondDouble(offset))
                            curve.KeySetValue(curve.KeyAdd(motionStart),(float)before[axis]);
                    using(var zero=FbxTime.FromSecondDouble(0))
                    {
                        var key=curve.KeyAdd(zero);curve.KeySetValue(key,(float)values[axis]);
                        curve.KeySetInterpolation(key,FbxAnimCurveDef.EInterpolationType.eInterpolationLinear);
                    }
                }
                finally { curve.KeyModifyEnd(); }
            }
            property.Set(new FbxDouble3(values[0],values[1],values[2]));
        }
    }
}
