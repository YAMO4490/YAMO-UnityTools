using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    /// <summary>Verify the actual imported Humanoid, not just its importer flags.</summary>
    internal static class MocapArmChainReplay
    {
        internal static void Validate(string genericPath,string humanPath,Avatar avatar,string prefix,
            int frames,int rate,string reportPath,Func<string,float,bool> progress)
        {
            var project=Path.GetFullPath(Path.Combine(Application.dataPath,"..")).Replace('\\','/').TrimEnd('/')+"/";
            Func<string,string> asset=p=>{var f=Path.GetFullPath(p).Replace('\\','/');return f.StartsWith(project+"Assets/",StringComparison.OrdinalIgnoreCase)?f.Substring(project.Length):null;};
            var gp=asset(genericPath);var hp=asset(humanPath);if(gp==null||hp==null)return;
            var generic=AssetDatabase.LoadAllAssetsAtPath(gp).OfType<AnimationClip>().Single(c=>!c.name.StartsWith("__preview__"));
            var human=AssetDatabase.LoadAllAssetsAtPath(hp).OfType<AnimationClip>().Single(c=>!c.name.StartsWith("__preview__"));
            var preview=EditorSceneManager.NewPreviewScene();GameObject g=null,h=null;var graph=default(PlayableGraph);
            double maxHandPosition=0,maxHandRotation=0,maxClavicle=0;var upper=new List<double>(frames*2);var fore=new List<double>(frames*2);
            try
            {
                g=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(gp),preview);
                h=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(avatar)),preview);
                var ga=g.GetComponent<Animator>();if(ga!=null){ga.avatar=null;ga.runtimeAnimatorController=null;}
                var animator=h.GetComponent<Animator>();animator.avatar=avatar;animator.runtimeAnimatorController=null;
                animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.Rebind();
                var gn=g.GetComponentsInChildren<Transform>(true).ToDictionary(t=>t.name,t=>t);
                var hn=h.GetComponentsInChildren<Transform>(true).ToDictionary(t=>t.name,t=>t);
                var names=new[]{" L Clavicle"," L UpperArm"," L Forearm"," L Hand"," R Clavicle"," R UpperArm"," R Forearm"," R Hand"}.Select(n=>prefix+n).ToArray();
                foreach(var n in names.Concat(new[]{prefix,prefix+" Neck"}))if(!gn.ContainsKey(n)||!hn.ContainsKey(n))throw new InvalidOperationException("재생 검증에 필요한 본이 없습니다: "+n);
                graph=PlayableGraph.Create("YAMO arm-chain replay validation");graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable=AnimationClipPlayable.Create(graph,human);playable.SetApplyFootIK(false);
                var output=AnimationPlayableOutput.Create(graph,"Humanoid",animator);output.SetSourcePlayable(playable);graph.Play();
                for(int f=0;f<frames;f++)
                {
                    var seconds=Math.Min(f/(double)rate,human.length);
                    generic.SampleAnimation(g,(float)seconds);playable.SetTime(seconds);graph.Evaluate(0);
                    var gi=BodyInverse(gn,prefix);var hi=BodyInverse(hn,prefix);
                    for(int i=0;i<names.Length;i++)
                    {
                        var G=gn[names[i]];var H=hn[names[i]];
                        var rotation=Angle(gi*G.rotation,hi*H.rotation);
                        if(i==1||i==5)upper.Add(rotation);
                        if(i==2||i==6)fore.Add(rotation);
                        if(i==0||i==4)maxClavicle=Math.Max(maxClavicle,rotation);
                        if(i==3||i==7)
                        {
                            maxHandRotation=Math.Max(maxHandRotation,rotation);
                            var P=gi*(G.position-gn[prefix].position);var Q=hi*(H.position-hn[prefix].position);
                            maxHandPosition=Math.Max(maxHandPosition,(P-Q).magnitude*1000.0);
                        }
                    }
                    if(f%120==0&&progress?.Invoke($"지정 Avatar 재생 검증 {f+1}/{frames}",.96f+.03f*f/frames)==true)
                        throw new OperationCanceledException("Avatar 재생 검증 취소");
                }
            }
            finally
            {
                if(graph.IsValid())graph.Destroy();if(g!=null)Object.DestroyImmediate(g);if(h!=null)Object.DestroyImmediate(h);
                EditorSceneManager.ClosePreviewScene(preview);
            }
            var upper95=Percentile95(upper);var fore95=Percentile95(fore);
            var passed=maxHandPosition<.5&&maxHandRotation<1&&upper95<5&&fore95<5;
            var report=new{passed,frames,rate,coordinateFrame="Hip/neck/shoulder body frame; meters converted to millimeters",handPositionMaxMm=maxHandPosition,handRotationMaxDeg=maxHandRotation,upperArmRotation95Deg=upper95,forearmRotation95Deg=fore95,clavicleRotationMaxDeg=maxClavicle,sourceAvatar=AssetDatabase.GetAssetPath(avatar),avatarGuid=AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(avatar))};
            File.WriteAllText(reportPath,JsonConvert.SerializeObject(report,Formatting.Indented));
            if(!passed)throw new InvalidOperationException("지정 Avatar 재생 오차가 허용 범위를 넘었습니다. 기존 출력을 복구합니다. "+JsonConvert.SerializeObject(report));
        }

        private static Quaternion BodyInverse(Dictionary<string,Transform> nodes,string prefix)
        {
            var up=(nodes[prefix+" Neck"].position-nodes[prefix].position).normalized;
            var right=(nodes[prefix+" R UpperArm"].position-nodes[prefix+" L UpperArm"].position).normalized;
            var forward=Vector3.Cross(right,up).normalized;
            if(forward.sqrMagnitude<.5f)throw new InvalidOperationException("Biped 몸통 기준 축을 구성할 수 없습니다.");
            return Quaternion.Inverse(Quaternion.LookRotation(forward,Vector3.Cross(forward,right).normalized));
        }
        private static double Angle(Quaternion a,Quaternion b)
        {
            var d=(double)a.x*b.x+(double)a.y*b.y+(double)a.z*b.z+(double)a.w*b.w;
            var aa=(double)a.x*a.x+(double)a.y*a.y+(double)a.z*a.z+(double)a.w*a.w;
            var bb=(double)b.x*b.x+(double)b.y*b.y+(double)b.z*b.z+(double)b.w*b.w;
            return 2*Math.Acos(Math.Min(1,Math.Abs(d)/Math.Sqrt(aa*bb)))*180/Math.PI;
        }
        private static double Percentile95(List<double> values)
        {
            values.Sort();var x=(values.Count-1)*.95;var i=(int)x;
            return values[i]+(values[Math.Min(i+1,values.Count-1)]-values[i])*(x-i);
        }
    }
}
