using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    [Serializable]
    public sealed class MocapArmChainSettings
    {
        public bool Enabled;
        public string BaseMaxPath;
        public string MaxBatchExecutable;
        public string MaxOutputDirectory;
        public string BipedRoot = "Bip001";
        public string ExportSelectionSet = "Export_Biped";
        public int RawTPoseFrame;
        public bool ApplyAvatarTwistProfile = true;

        public static string DefaultMaxBatchExecutable => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Autodesk", "3ds Max 2025", "3dsmaxbatch.exe");
    }

    public sealed class MocapArmChainResult
    {
        public string MaxPath { get; internal set; }
        public string GenericFbxPath { get; internal set; }
        public string HumanoidFbxPath { get; internal set; }
        public string ValidationPath { get; internal set; }
        public string UnityReplayValidationPath { get; internal set; }
        public string HandEulerOrder { get; internal set; }
        public int FrameCount { get; internal set; }
    }

    /// <summary>
    /// Shared OptiTrack/MMRP path: untouched raw input, bounded window bake,
    /// native Biped hand-goal solve, MAX reload, explicit FK export, verified promotion.
    /// </summary>
    public static class MocapArmChainPipeline
    {
        private const string Package = "Packages/com.yamo.unitytools";
        private static bool running;
        public static bool IsRunning => running;
        private static string Bridge => Path.Combine(
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath(Package).resolvedPath,
            "Tools~", "ArmChain");

        public static List<MocapPipelineResult> Run(
            MocapPipelineSettings settings, IReadOnlyList<MocapPipelineItem> items,
            Func<string, float, bool> progress = null)
        {
            // Preserved for a future separate Max script/batch application.
            // This Unity tool must never launch 3ds Max.
            throw new NotSupportedException("3ds Max 자동 배치는 분리되었습니다. Unity 리타게팅·FBX 출력 파이프라인을 사용하세요.");
#if false
            Validate(settings, items);
            if (running) throw new InvalidOperationException("팔 체인 파이프라인이 이미 실행 중입니다.");
            running = true;
            var token = DateTime.Now.ToString("HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var work = Path.Combine(project, "Codex", DateTime.Now.ToString("yyyy-MM-dd"), "MocapArmChain", token);
            var assets = $"Assets/Codex/{DateTime.Now:yyyy-MM-dd}/MocapArmChain/{token}";
            Directory.CreateDirectory(work);
            var results = new List<MocapPipelineResult>();
            try
            {
                // Validate all sources before changing the target Avatar or making binding copies.
                var queue = items.Where(i => i != null && i.Enabled && i.SourceFbx != null).ToArray();
                foreach (var item in queue)
                {
                    var source = AssetDatabase.GetAssetPath(item.SourceFbx);
                    if (!MocapPipelineSourceUtility.IsFbxModel(item.SourceFbx))
                        throw new InvalidOperationException("Raw 손 보존은 FBX 입력이 필요합니다. .anim 직접 입력은 기존 베이크 모드를 사용하세요.");
                    var error = OptiTrackMotionBindingService.ValidateSourceFormat(source, settings.SourceFormat);
                    if (error != null) throw new InvalidOperationException(error);
                    if (Path.GetFileNameWithoutExtension(source).EndsWith("_T", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("_T 파일은 Raw 입력이 아닙니다.");
                    ResolveRawArmBones(AssetDatabase.LoadAssetAtPath<GameObject>(source)
                        .GetComponentsInChildren<Transform>(true).Select(t => t.name), settings.SourceFormat);
                }
                var avatar = PrepareAvatar(settings, work);
                EnsureFolder(assets);
                var planned = OptiTrackMotionBindingService.PlanAnimationNames(
                    queue.Select(i => AssetDatabase.GetAssetPath(i.SourceFbx)), settings.SourceFormat, out _);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < queue.Length; index++)
                {
                    var item = queue[index];
                    var result = new MocapPipelineResult { Item = item, Stage = MocapPipelineStage.Pending };
                    results.Add(result);
                    try
                    {
                        var source = AssetDatabase.GetAssetPath(item.SourceFbx);
                        planned.TryGetValue(source, out var fallback);
                        var name = MocapPipelineOutputNaming.MakeUnique(item,
                            fallback ?? Path.GetFileNameWithoutExtension(source), source, used);
                        var jobWork = Path.Combine(work, index.ToString("D4") + "_" + name);
                        var jobAssets = assets + "/" + index.ToString("D4");
                        Directory.CreateDirectory(jobWork); EnsureFolder(jobAssets);
                        Func<string, float, bool> itemProgress = (message, fraction) =>
                            progress?.Invoke($"[{index + 1}/{queue.Length}] {message}",
                                (index + Mathf.Clamp01(fraction)) / queue.Length) == true;
                        RunItem(settings, item, result, avatar, source, name, jobWork, jobAssets, itemProgress);
                        result.Stage = MocapPipelineStage.Completed;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        result.Stage = MocapPipelineStage.Failed; result.Error = e.Message;
                        UnityEngine.Debug.LogException(e);
                        if (!settings.ContinueOnError) throw;
                    }
                    finally
                    {
                        GC.Collect();
                    }
                }
                File.WriteAllText(Path.Combine(work, "batch-result.json"), JsonConvert.SerializeObject(
                    results.Select(r => new { source = AssetDatabase.GetAssetPath(r.Item.SourceFbx),
                        stage = r.Stage.ToString(), r.Error, output = r.ArmChain }), Formatting.Indented));
                return results;
            }
            finally { running = false; EditorUtility.ClearProgressBar(); }
#endif
        }

        private static void RunItem(MocapPipelineSettings settings, MocapPipelineItem item,
            MocapPipelineResult result, Avatar avatar, string source, string name,
            string work, string assets, Func<string, float, bool> progress)
        {
            var options = settings.ArmChain;
            var rawHash = Hash(source); var rawMetaHash = Hash(source + ".meta");
            var rawNames = ResolveRawArmBones(AssetDatabase.LoadAssetAtPath<GameObject>(source)
                .GetComponentsInChildren<Transform>(true).Select(t => t.name), settings.SourceFormat);
            var importer = (ModelImporter)AssetImporter.GetAtPath(source);
            var originalRanges = importer.clipAnimations;
            if (originalRanges.Length == 0) originalRanges = importer.defaultClipAnimations;
            if (originalRanges.Length != 1 || importer.defaultClipAnimations.Length != 1)
                throw new InvalidOperationException("팔 체인 보존은 FBX의 단일 Take/클립 입력을 지원합니다.");
            var originalClip = AssetDatabase.LoadAllAssetsAtPath(source).OfType<AnimationClip>()
                .Single(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal));
            var sourceRate = originalClip.frameRate;
            var duration = item.Duration > 0 ? item.Duration : originalClip.length - item.StartTime;
            if (item.StartTime < 0 || duration <= 0 || item.StartTime + duration > originalClip.length + .001f)
                throw new InvalidOperationException("선택한 모션 구간이 원본 클립 범위를 벗어났습니다.");
            var end = Mathf.CeilToInt(duration * settings.SampleRate);
            if (end < 1 || end > 50000) throw new InvalidOperationException("한 항목은 2~50,001개 샘플이어야 합니다. 긴 모션은 구간을 나누세요.");
            var rawStart = originalRanges[0].firstFrame / sourceRate + item.StartTime;
            var rawReference = options.RawTPoseFrame / (double)sourceRate;
            CheckCancel(progress, "원본 보존과 바인딩 복사본 준비", .01f);
            result.Stage = MocapPipelineStage.BackingUpSource;
            var rawBackup = Path.Combine(work, "Original.fbx");
            File.Copy(source, rawBackup, false); File.Copy(source + ".meta", rawBackup + ".meta", false);
            result.SourceBackupPath = rawBackup; result.SourceBackupCreated = true;
            var copy = assets + "/" + Path.GetFileName(source);
            if (!MocapFbxAssetCopy.Copy(source, copy)) throw new IOException("입력 작업 복사본 생성 실패");
            var copiedImporter = (ModelImporter)AssetImporter.GetAtPath(copy);
            var fullRanges = copiedImporter.defaultClipAnimations;
            var takeStart = fullRanges[0].firstFrame / sourceRate;
            copiedImporter.animationType = ModelImporterAnimationType.Generic;
            copiedImporter.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            copiedImporter.sourceAvatar = null; copiedImporter.userData = string.Empty;
            copiedImporter.importAnimation = true; copiedImporter.animationCompression = ModelImporterAnimationCompression.Off;
            copiedImporter.clipAnimations = fullRanges; copiedImporter.SaveAndReimport();
            result.Stage = MocapPipelineStage.Binding;
            result.Binding = OptiTrackMotionBindingService.Process(copy, ExistingMotionAssetPolicy.Fail,
                Path.GetFileNameWithoutExtension(copy), settings.SourceFormat);
            if (!result.Binding.Succeeded) throw new InvalidOperationException(result.Binding.Note);
            var clip = result.Binding.AnimationClip;
            var referenceTime = (float)(rawReference - takeStart);
            var bodyTime = rawStart - takeStart;
            if (referenceTime < 0 || referenceTime + 1f / settings.SampleRate > clip.length + .001f)
                throw new InvalidOperationException("원본 T포즈 프레임이 전체 Take 범위를 벗어났습니다.");
            var preview = EditorSceneManager.NewPreviewScene();
            GameObject clone = null;
            string bodyPath = Path.Combine(work, "body.fbx"), referencePath = Path.Combine(work, "reference.fbx");
            try
            {
                var avatarModel = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(avatar));
                if (avatarModel == null) throw new InvalidOperationException("FBX에서 생성된 대상 Biped Avatar가 필요합니다.");
                clone = (GameObject)PrefabUtility.InstantiatePrefab(avatarModel, preview);
                var animator = clone.GetComponent<Animator>();
                if (animator == null) animator = clone.AddComponent<Animator>();
                animator.avatar = avatar; animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                result.Stage = MocapPipelineStage.HingeBake;
                BakeWindow(animator, clip, bodyTime, duration, bodyPath, settings.SampleRate,
                    (m,p) => progress?.Invoke(m, Mathf.Lerp(.05f, .25f, p)) == true);
                BakeWindow(animator, clip, referenceTime, 1f / settings.SampleRate, referencePath, settings.SampleRate,
                    (m,p) => progress?.Invoke(m, Mathf.Lerp(.25f, .28f, p)) == true);
            }
            finally { if (clone != null) Object.DestroyImmediate(clone); EditorSceneManager.ClosePreviewScene(preview); }
            // All Unity sampling objects are gone before the external Max process starts.
            var bodyWithReference = Path.Combine(work, "body-with-reference.fbx");
            MocapArmChainReference.Create(bodyPath, referencePath, bodyWithReference, settings.SampleRate);
            result.Binding.AnimationClip = null;
            clip = null;
            originalClip = null;
            CheckCancel(progress, "Unity 샘플링 메모리 반환", .29f);
            EditorUtility.UnloadUnusedAssetsImmediate(true);
            GC.Collect();
            var token = Guid.NewGuid().ToString("N");
            var job = new { token, name, workDirectory = work,
                nativePlugin = Path.Combine(Bridge, "Binaries", "2025", "YamoArmChain.gup"),
                sampleRate = settings.SampleRate, endFrame = end, bipedRoot = options.BipedRoot,
                rawFbx = Path.GetFullPath(source), rawSha256 = rawHash,
                baseMax = Path.GetFullPath(options.BaseMaxPath), baseSha256 = Hash(options.BaseMaxPath),
                rawBoneNames = rawNames, rawStartSeconds = (double)rawStart, rawReferenceSeconds = rawReference,
                rawDurationSeconds = (double)duration, referenceFbx = referencePath,
                bodyFbx = bodyWithReference, selectionSet = options.ExportSelectionSet };
            var jobPath = Path.Combine(work, "job.json");
            File.WriteAllText(jobPath, JsonConvert.SerializeObject(job, Formatting.Indented));
            var wrapper = Path.Combine(work, "launch.py");
            File.WriteAllText(wrapper, "import runpy,sys\nsys.argv=[" + PythonString(Path.Combine(Bridge, "run_max.py")) +
                "," + PythonString(jobPath) + "]\nrunpy.run_path(sys.argv[0],run_name='__main__')\n");
            result.Stage = MocapPipelineStage.ExportingFbx;
            RunMax(options.MaxBatchExecutable, wrapper, work, token, progress);
            var native = JObject.Parse(File.ReadAllText(Path.Combine(work, "result.json")));
            if (!(bool)native["passed"] || (string)native["token"] != token ||
                Hash((string)native["fbx"]) != (string)native["fbxSha256"] ||
                Hash((string)native["max"]) != (string)native["maxSha256"])
                throw new InvalidOperationException("MAX/FBX 검증 결과 또는 파일 해시가 올바르지 않습니다.");
            if (Hash(source) != rawHash || Hash(source + ".meta") != rawMetaHash)
                throw new InvalidOperationException("작업 중 원본 FBX 또는 importer가 변경되었습니다.");
            CheckCancel(progress, "검증된 MAX·FBX 결과 반영", .94f);
            var fbxFolder = Path.Combine(Path.GetFullPath(settings.FbxOutputDirectory), name);
            var maxFolder = Path.Combine(Path.GetFullPath(options.MaxOutputDirectory), name);
            Directory.CreateDirectory(fbxFolder); Directory.CreateDirectory(maxFolder);
            var genericPath = Path.Combine(fbxFolder, name + ".fbx");
            var humanPath = Path.Combine(fbxFolder, "Ani_" + name + ".fbx");
            var maxPath = Path.Combine(maxFolder, "Ani_" + name + ".max");
            if (SamePath(maxPath, options.BaseMaxPath) || SamePath(genericPath, source) || SamePath(humanPath, source))
                throw new InvalidOperationException("출력 경로가 기준 MAX 또는 Raw 원본과 같습니다. 원본은 덮어쓸 수 없습니다.");
            var oldGeneric = ExistingClipAt(genericPath); var oldHuman = ExistingClipAt(humanPath);
            var publication = new[] {
                new Publication((string)native["max"], maxPath, Path.Combine(work,"Previous","Max")),
                new Publication((string)native["fbx"], genericPath, Path.Combine(work,"Previous","Generic")),
                new Publication((string)native["fbx"], humanPath, Path.Combine(work,"Previous","Human")) };
            try
            {
                foreach(var output in publication) output.Publish();
                ImportOutput(genericPath, name, end, settings.SampleRate, null, oldGeneric);
                ImportOutput(humanPath, "Ani_" + name, end, settings.SampleRate, avatar, oldHuman);
                MocapArmChainReplay.Validate(genericPath, humanPath, avatar, options.BipedRoot,
                    end + 1, settings.SampleRate, Path.Combine(work, "unity-replay.json"), progress);
            }
            catch
            {
                foreach(var output in publication.Reverse()) output.Rollback();
                throw;
            }
            result.ArmChain = new MocapArmChainResult { MaxPath = maxPath, GenericFbxPath = genericPath,
                HumanoidFbxPath = humanPath, ValidationPath = Path.Combine(work, "result.json"),
                UnityReplayValidationPath = File.Exists(Path.Combine(work, "unity-replay.json"))
                    ? Path.Combine(work, "unity-replay.json") : null,
                HandEulerOrder = (string)native["handEulerOrder"], FrameCount = end + 1 };
            result.FbxExport = new BipedFbxExportResult { OutputPath = humanPath, SampleCount = end + 1 };
            File.WriteAllText(Path.Combine(work, "published.json"), JsonConvert.SerializeObject(result.ArmChain, Formatting.Indented));
            progress?.Invoke("팔 체인 보존·MAX 재로드·FBX 추출 완료", 1f);
        }

        private static void BakeWindow(Animator animator, AnimationClip source, float start, float duration,
            string output, int rate, Func<string, float, bool> progress)
        {
            ForearmHingeBakeResult baked = null;
            try
            {
                baked = ForearmHingeBakeService.BakeEditMode(animator, source,
                    new ForearmHingeBakeSettings { SampleRate = rate, EnableHingeCorrection = false,
                        StartTime = start, Duration = duration }, (m,p) => progress?.Invoke(m, p*.65f) == true);
                BipedFbxExportService.Export(new BipedFbxExportSettings {
                    TargetRoot = animator.gameObject, Clip = baked.Clip, OutputPath = output,
                    StartTime = 0, Duration = baked.Clip.length, FrameRate = rate,
                    RecordBlendShapes = false, Compression = MotionFbxCurveCompression.Disabled,
                    ExportGeometry = false, ExportUnrendered = true, UseCompatibleNames = false,
                    CreateBackup = false }, (m,p) => progress?.Invoke(m, .65f + p*.35f) == true);
            }
            finally { if (baked?.Clip != null) Object.DestroyImmediate(baked.Clip); }
        }

        private static Avatar PrepareAvatar(MocapPipelineSettings settings, string work)
        {
            var avatar = settings.TargetAnimator.avatar;
            var hd = avatar.humanDescription;
            if (Mathf.Approximately(hd.upperArmTwist,0.5f) && Mathf.Approximately(hd.lowerArmTwist,0)) return avatar;
            if (!settings.ArmChain.ApplyAvatarTwistProfile)
                throw new InvalidOperationException("이 팔 보존 프리셋은 대상 Avatar Upper Arm Twist 0.5 / Lower Arm Twist 0이 필요합니다.");
            var path = AssetDatabase.GetAssetPath(avatar);
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null || importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
                throw new InvalidOperationException("Twist 자동 적용은 FBX에서 직접 생성한 대상 Avatar가 필요합니다.");
            File.Copy(path + ".meta", Path.Combine(work, "Avatar-before.fbx.meta"), false);
            var description = importer.humanDescription;
            description.upperArmTwist = 0.5f; description.lowerArmTwist = 0;
            importer.humanDescription = description; importer.SaveAndReimport();
            avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().Single();
            if (!avatar.isValid || !avatar.isHuman || !Mathf.Approximately(avatar.humanDescription.upperArmTwist,0.5f) ||
                !Mathf.Approximately(avatar.humanDescription.lowerArmTwist,0))
                throw new InvalidOperationException("대상 Avatar Twist 적용 검증 실패");
            return avatar;
        }

        public static string[] ResolveRawArmBones(IEnumerable<string> boneNames, MocapSourceFormat format)
        {
            var names = boneNames.ToArray();
            var slots = new[] { "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
                "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand" };
            if (format == MocapSourceFormat.OptiTrack)
            {
                var roots = names.Where(n => n.EndsWith("_Hips", StringComparison.Ordinal)).ToArray();
                if (roots.Length != 1) throw new InvalidOperationException("OptiTrack FBX에는 단일 액터의 _Hips가 필요합니다.");
                var prefix = roots[0].Substring(0, roots[0].Length - "_Hips".Length);
                slots = new[] { "_LeftShoulder", "_LeftArm", "_LeftForeArm", "_LeftHand",
                    "_RightShoulder", "_RightArm", "_RightForeArm", "_RightHand" }.Select(s => prefix+s).ToArray();
            }
            var alternatives = new[] { "LShoulder", "LUArm", "LFArm", "LHand", "RShoulder", "RUArm", "RFArm", "RHand" };
            var result = new string[8];
            for (var i=0;i<8;i++)
            {
                var candidates = names.Where(n => n==slots[i] || format==MocapSourceFormat.MMRP && n==alternatives[i]).ToArray();
                if (candidates.Length!=1) throw new InvalidOperationException($"Raw 팔 본을 하나로 결정할 수 없습니다: {slots[i]}");
                result[i]=candidates[0];
            }
            return result;
        }

        private static void RunMax(string executable, string wrapper, string work, string token,
            Func<string, float, bool> progress)
        {
            var start = new ProcessStartInfo { FileName=Path.GetFullPath(executable),
                Arguments="\""+wrapper+"\"", WorkingDirectory=work, UseShellExecute=false,
                CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardOutput=true, RedirectStandardError=true };
            using (var log = new StreamWriter(Path.Combine(work,"max-process.log")))
            using (var process = new Process { StartInfo=start })
            {
                var gate=new object();
                process.OutputDataReceived+=(s,e)=>{if(e.Data!=null)lock(gate){log.WriteLine(e.Data.Replace("\0",string.Empty));log.Flush();}};
                process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(gate){log.WriteLine(e.Data.Replace("\0",string.Empty));log.Flush();}};
                if (!process.Start()) throw new InvalidOperationException("3ds Max 배치 실행 실패");
                process.BeginOutputReadLine();process.BeginErrorReadLine();
                try
                {
                    while (!process.WaitForExit(200))
                    {
                        var stage="MAX 배치 처리 중";
                        var path=Path.Combine(work,"progress.json");
                        try {if(File.Exists(path))stage=(string)JObject.Parse(File.ReadAllText(path))["stage"];} catch(IOException) { }
                        CheckCancel(progress,stage,.5f);
                    }
                    process.WaitForExit();
                }
                catch
                {
                    // The PID is written by this job's own worker, never selected from an interactive Max window.
                    try
                    {
                        var path=Path.Combine(work,"progress.json");
                        if(File.Exists(path))
                        {
                            var p=JObject.Parse(File.ReadAllText(path));
                            if((string)p["token"]==token)
                            {
                                using(var worker=Process.GetProcessById((int)p["pid"]))
                                    if(worker.StartTime>=process.StartTime.AddSeconds(-2) &&
                                        (worker.ProcessName.Equals("3dsmax",StringComparison.OrdinalIgnoreCase)||worker.ProcessName.Equals("3dsmaxbatch",StringComparison.OrdinalIgnoreCase)))
                                        worker.Kill();
                            }
                        }
                    } catch { }
                    try {if(!process.HasExited)process.Kill();}catch { }
                    throw;
                }
                var failure=Path.Combine(work,"failure.json");
                if(process.ExitCode!=0 || File.Exists(failure) || !File.Exists(Path.Combine(work,"result.json")))
                    throw new InvalidOperationException(File.Exists(failure)?(string)JObject.Parse(File.ReadAllText(failure))["error"]:
                        $"MAX 배치가 결과를 생성하지 못했습니다 (exit {process.ExitCode}). 로그: {work}");
            }
        }

        private sealed class ClipIdentity
        {
            internal string Guid, Name;
            internal long Id;
        }

        private static string AssetPath(string fullPath)
        {
            var project=Path.GetFullPath(Path.Combine(Application.dataPath,"..")).Replace('\\','/').TrimEnd('/')+"/";
            var full=Path.GetFullPath(fullPath).Replace('\\','/');
            return full.StartsWith(project+"Assets/",StringComparison.OrdinalIgnoreCase)?full.Substring(project.Length):null;
        }

        private static ClipIdentity ExistingClipAt(string fullPath)
        {
            var path=AssetPath(fullPath);if(path==null||!File.Exists(fullPath))return null;
            var old=AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c=>!c.name.StartsWith("__preview__"));
            if(old==null)return null;
            string guid;long id;AssetDatabase.TryGetGUIDAndLocalFileIdentifier(old,out guid,out id);
            return new ClipIdentity { Guid=guid,Id=id,Name=old.name };
        }

        private static void ImportOutput(string fullPath, string desiredName, int end, int rate, Avatar avatar, ClipIdentity old)
        {
            var path=AssetPath(fullPath);if(path==null)return;
            if(old!=null)desiredName=old.Name;
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
            var imp=(ModelImporter)AssetImporter.GetAtPath(path);
            imp.importAnimation=true;imp.animationType=avatar==null?ModelImporterAnimationType.Generic:ModelImporterAnimationType.Human;
            imp.avatarSetup=avatar==null?ModelImporterAvatarSetup.CreateFromThisModel:ModelImporterAvatarSetup.CopyFromOther;
            imp.sourceAvatar=avatar;imp.animationCompression=ModelImporterAnimationCompression.Off;imp.resampleCurves=true;
            var ranges=imp.defaultClipAnimations;
            if(ranges.Length!=1)throw new InvalidOperationException("최종 FBX에 단일 Take가 필요합니다.");
            ranges[0].name=desiredName;ranges[0].firstFrame=0;ranges[0].lastFrame=end;ranges[0].loopTime=false;
            if(old!=null)typeof(ModelImporterClipAnimation).GetField("internalID",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic)?.SetValue(ranges[0],old.Id);
            imp.clipAnimations=ranges;imp.SaveAndReimport();
            var clip=AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Single(c=>!c.name.StartsWith("__preview__"));
            string guid;long id;AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip,out guid,out id);
            if(old!=null&&(old.Id!=id||old.Guid!=guid))throw new InvalidOperationException("기존 클립 GUID/ID 보존 실패: "+path);
            if(clip.humanMotion!=(avatar!=null)||Mathf.Abs(clip.frameRate-rate)>.01f||Mathf.Abs(clip.length-end/(float)rate)>.001f)
                throw new InvalidOperationException("최종 FBX 형식/범위 검증 실패: "+path);
        }

        private sealed class Publication
        {
            private readonly string source,destination,backup;
            private bool written;
            private readonly bool existed;
            internal Publication(string source,string destination,string backup)
            {
                this.source=source;this.destination=destination;this.backup=backup;existed=File.Exists(destination);
            }
            internal void Publish()
            {
                if(existed)
                {
                    Directory.CreateDirectory(backup);
                    File.Copy(destination,Path.Combine(backup,Path.GetFileName(destination)),false);
                    if(File.Exists(destination+".meta"))File.Copy(destination+".meta",Path.Combine(backup,Path.GetFileName(destination)+".meta"),false);
                }
                var temporary=destination+".armchain-partial";
                File.Copy(source,temporary,false);
                if(Hash(source)!=Hash(temporary))throw new IOException("결과 복사 해시 불일치");
                written=true;File.Copy(temporary,destination,true);File.Delete(temporary);
            }
            internal void Rollback()
            {
                if(!written)return;
                var path=AssetPath(destination);
                if(existed)
                {
                    File.Copy(Path.Combine(backup,Path.GetFileName(destination)),destination,true);
                    var meta=Path.Combine(backup,Path.GetFileName(destination)+".meta");
                    if(File.Exists(meta))File.Copy(meta,destination+".meta",true);
                    if(path!=null)AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
                }
                else if(path!=null)
                {
                    if(!AssetDatabase.DeleteAsset(path))FileUtil.DeleteFileOrDirectory(path);
                }
                else File.Delete(destination);
            }
        }

        private static void Validate(MocapPipelineSettings settings,IReadOnlyList<MocapPipelineItem> items)
        {
            if(settings?.ArmChain==null||!settings.ArmChain.Enabled)throw new ArgumentException("팔 체인 설정이 없습니다.");
            if(settings.TargetAnimator==null||settings.TargetAnimator.avatar==null||!settings.TargetAnimator.avatar.isHuman||!settings.TargetAnimator.avatar.isValid)
                throw new InvalidOperationException("유효한 대상 Biped Animator/Avatar가 필요합니다.");
            if(EditorApplication.isCompiling||EditorApplication.isPlayingOrWillChangePlaymode||AnimationMode.InAnimationMode())
                throw new InvalidOperationException("컴파일/Play Mode/Animation Preview가 끝난 뒤 실행하세요.");
            if(settings.SampleRate<=0||4800%settings.SampleRate!=0)throw new InvalidOperationException("Sample Rate는 4800을 나누어 떨어지는 값이어야 합니다 (예: 30/60/120).");
            if(items==null||!items.Any(i=>i!=null&&i.Enabled&&i.SourceFbx!=null))throw new InvalidOperationException("활성화된 Raw FBX가 없습니다.");
            var a=settings.ArmChain;
            var targetNames=settings.TargetAnimator.GetComponentsInChildren<Transform>(true).Select(t=>t.name).ToArray();
            foreach(var side in new[]{" L "," R "})foreach(var bone in new[]{"Clavicle","UpperArm","Forearm","Hand"})
                if(!targetNames.Contains(a.BipedRoot+side+bone))throw new InvalidOperationException("대상 Animator에서 Biped 본을 찾을 수 없습니다: "+a.BipedRoot+side+bone);
            if(!File.Exists(a.BaseMaxPath)||!a.BaseMaxPath.EndsWith(".max",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("기준 MAX 파일을 지정하세요.");
            if(!File.Exists(a.MaxBatchExecutable)||!Path.GetFileName(a.MaxBatchExecutable).Equals("3dsmaxbatch.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("3ds Max 2025의 3dsmaxbatch.exe를 지정하세요.");
            var version=FileVersionInfo.GetVersionInfo(a.MaxBatchExecutable).FileMajorPart;
            if(version!=27)throw new InvalidOperationException("동봉된 네이티브 브리지는 3ds Max 2025용입니다. 다른 버전에는 해당 SDK로 빌드한 브리지가 필요합니다.");
            if(!File.Exists(Path.Combine(Bridge,"Binaries","2025","YamoArmChain.gup")))throw new FileNotFoundException("네이티브 팔 체인 브리지가 없습니다.");
            if(string.IsNullOrWhiteSpace(settings.FbxOutputDirectory)||string.IsNullOrWhiteSpace(a.MaxOutputDirectory)||string.IsNullOrWhiteSpace(a.BipedRoot)||string.IsNullOrWhiteSpace(a.ExportSelectionSet)||a.RawTPoseFrame<0)
                throw new InvalidOperationException("FBX/MAX 출력 폴더와 Biped/Selection Set/T포즈 설정을 확인하세요.");
            var assets=Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(a.MaxOutputDirectory).StartsWith(assets,StringComparison.OrdinalIgnoreCase)||SamePath(a.MaxOutputDirectory,Application.dataPath))throw new InvalidOperationException("MAX 출력 폴더는 Unity Assets 밖으로 지정하세요.");
        }

        private static string Hash(string path)
        {
            using(var sha=System.Security.Cryptography.SHA256.Create())using(var f=File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-",string.Empty).ToLowerInvariant();
        }
        private static string PythonString(string text)=>JsonConvert.SerializeObject(Path.GetFullPath(text));
        private static bool SamePath(string a,string b)=>string.Equals(Path.GetFullPath(a).TrimEnd('\\','/'),Path.GetFullPath(b).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);
        private static void CheckCancel(Func<string,float,bool> callback,string message,float p)
        {if(callback?.Invoke(message,p)==true)throw new OperationCanceledException("팔 체인 처리가 취소됐습니다. 원본과 검증 전 작업 파일은 보존했습니다.");}
        private static void EnsureFolder(string path)
        {
            if(AssetDatabase.IsValidFolder(path))return;
            var parent=Path.GetDirectoryName(path).Replace('\\','/');EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent,Path.GetFileName(path));
        }
    }
}
