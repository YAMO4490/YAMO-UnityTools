using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YAMO.UnityTools
{
    [Serializable]
    public sealed class YamoFacialTarget
    {
        public string Label;
        [Tooltip("클립을 재생할 Animator가 있는 루트. 페이셜 단독은 Avatar 없이도 사용 가능합니다.")]
        public Transform Root;
        public SkinnedMeshRenderer[] FaceMeshes = Array.Empty<SkinnedMeshRenderer>();
        public Transform Head;
        public Transform LeftEye;
        public Transform RightEye;
    }

    public enum YamoFacialRecordingMode { FaceOnly, FaceAndHeadEyes }

    /// <summary>Independent live facial receiver and clip recorder. No EasyMotionRecorder dependency.</summary>
    [AddComponentMenu("YAMO/Animation/Live Facial Recorder")]
    [DefaultExecutionOrder(31000)]
    public sealed class YamoFacialRecorder : MonoBehaviour
    {
        public List<YamoFacialTarget> Targets = new List<YamoFacialTarget>();
        [Min(0)] public int InitialTarget;
        public string DeviceAddress = "255.255.255.255";
        [Range(1, 65535)] public int Port = 49983;
        public bool Mirror = true;
        public bool ReceiveHeadAndEyes;
        public YamoFacialRecordingMode RecordingMode = YamoFacialRecordingMode.FaceOnly;
        [Range(1, 120)] public int SampleRate = 60;
        public string OutputFolder = "Assets/Motion/YAMOFacial";
        public List<string> ExcludedBlendShapes = new List<string>();
        public bool ShowOverlay = true;
        [Range(.5f, 3f)] public float OverlayScale = 1.25f;
        public bool EnableHotkeys = true;
        public KeyCode RecordKey = KeyCode.R;
        public KeyCode StopKey = KeyCode.X;

        public int ActiveTargetIndex { get; private set; } = -1;
        public bool IsRecording { get; private set; }
        public bool IsReceiving => session != null && session.Worker.IsAlive && session.Error == null;
        public bool HasUnsavedTake => take != null && !IsRecording;
        public string Status { get; private set; } = "수신 대기";
        public string LastSavedPath { get; private set; }
        public float RecordingSeconds => IsRecording ? Time.realtimeSinceStartup - startTime : 0f;
        public bool HasRecentPacket => IsReceiving && Time.realtimeSinceStartup - lastPacketAt < 2f;
        public int ReceivedPackets => session != null ? Volatile.Read(ref session.PacketCount) : 0;
        public int ParsedPackets { get; private set; }
        public string LastPacketPreview { get; private set; }
        public string LastSender => session != null ? session.Sender : null;

        private sealed class Session
        {
            internal UdpClient Socket;
            internal Thread Worker;
            internal volatile bool Stop;
            internal volatile string Latest;
            internal volatile string Error;
            internal int PacketCount;
            internal volatile string Sender;
        }
        private sealed class FaceBinding
        {
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Mesh;
            internal Dictionary<string, int> Indices;
        }
        private sealed class Channel
        {
            internal UnityEngine.Object Target;
            internal Mesh Mesh;
            internal string Path;
            internal string Property;
            internal int Index;
            internal AnimationCurve Curve = new AnimationCurve();
        }
        private sealed class Take
        {
            internal readonly List<Channel> Channels = new List<Channel>();
            internal string Label;
            internal string Folder;
            internal int Rate;
            internal YamoFacialRecordingMode Mode;
        }
        private Session session;
        private readonly List<FaceBinding> faces = new List<FaceBinding>();
        private Transform head, leftEye, rightEye;
        private Take take;
        private float startTime, lastSample, lastPacketAt = float.NegativeInfinity;
        private float lastConnectRequest, lastRawPacketAt = float.NegativeInfinity;
        private Vector2 scroll;
        private GUIStyle headerStyle, statusStyle;

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            SelectTarget(InitialTarget);
            Connect();
        }

        private void OnDisable()
        {
            StopRecording();
            Disconnect();
        }

        public void Connect()
        {
            if (!Application.isPlaying) { Status = "Play Mode에서 연결하세요."; return; }
            Disconnect();
            try
            {
                var current = new Session { Socket = new UdpClient(Port) };
                current.Socket.Client.ReceiveTimeout = 250;
                current.Worker = new Thread(() => Receive(current)) { IsBackground = true, Name = "YAMO Facial UDP" };
                session = current;
                current.Worker.Start();
                lastPacketAt = float.NegativeInfinity;
                lastRawPacketAt = float.NegativeInfinity;
                ParsedPackets = 0;
                LastPacketPreview = null;
                Status = "UDP 연결됨 · 데이터 대기";
                // The two apps use different start-stream commands and destination ports.
                RequestStream();
            }
            catch (Exception error)
            {
                Disconnect();
                Status = "연결 실패: " + error.Message + " (기존 수신기의 같은 포트 사용 여부 확인)";
                Debug.LogWarning("[YAMO Facial] " + Status, this);
            }
        }

        private void RequestStream()
        {
            lastConnectRequest = Time.realtimeSinceStartup;
            SendStart("iFacialMocap_sahuasouryya9218sauhuiayeta91555dy3719", 49983);
            SendStart("FACEMOTION3D_OtherStreaming", 49993);
        }

        private void SendStart(string command, int port)
        {
            try
            {
                using (var sender = new UdpClient())
                {
                    sender.EnableBroadcast = true;
                    var bytes = Encoding.UTF8.GetBytes(command);
                    // Repeat the start request as the reference receiver did; UDP is lossy.
                    for (var i = 0; i < 5; i++) sender.Send(bytes, bytes.Length, DeviceAddress, port);
                }
            }
            catch (Exception error) { Status = "UDP 수신 준비됨 · 기기 연결 요청 실패: " + error.Message; }
        }

        private static void Receive(Session current)
        {
            while (!current.Stop)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    current.Latest = Encoding.UTF8.GetString(current.Socket.Receive(ref remote));
                    current.Sender = remote.Address.ToString();
                    Interlocked.Increment(ref current.PacketCount);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException error)
                {
                    if (current.Stop) break;
                    if (error.SocketErrorCode != SocketError.TimedOut) { current.Error = error.Message; break; }
                }
                catch (Exception error) { if (!current.Stop) current.Error = error.Message; break; }
            }
        }

        public void Disconnect()
        {
            var current = session;
            session = null;
            if (current == null) return;
            current.Stop = true;
            current.Socket.Close();
            if (current.Worker != null && current.Worker != Thread.CurrentThread) current.Worker.Join(500);
            lastPacketAt = float.NegativeInfinity;
        }

        public bool SelectTarget(int index)
        {
            if (index < 0 || index >= Targets.Count)
            {
                Status = "등록되지 않은 캐릭터 슬롯입니다.";
                return false;
            }
            if (!ValidateTarget(Targets[index], out var error))
            {
                Status = error;
                return false;
            }
            var resume = IsRecording;
            if (!StopRecording()) return false;
            ActiveTargetIndex = index;
            var selected = Targets[index];
            faces.Clear();
            foreach (var renderer in selected.FaceMeshes.Distinct())
            {
                var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                    indices[YamoFacialPacket.NormalizeName(renderer.sharedMesh.GetBlendShapeName(i))] = i;
                faces.Add(new FaceBinding { Renderer = renderer, Mesh = renderer.sharedMesh, Indices = indices });
            }
            head = selected.Head; leftEye = selected.LeftEye; rightEye = selected.RightEye;
            Status = "선택: " + TargetLabel(index);
            return !resume || StartRecording();
        }

        public static bool ValidateTarget(YamoFacialTarget target, out string error)
        {
            error = null;
            if (target == null || target.Root == null) { error = "캐릭터 루트를 지정하세요."; return false; }
            if (target.FaceMeshes == null || target.FaceMeshes.Length == 0) { error = "얼굴 메시를 지정하세요."; return false; }
            var paths = new HashSet<string>();
            foreach (var face in target.FaceMeshes.Distinct())
            {
                if (face == null || face.sharedMesh == null || face.sharedMesh.blendShapeCount == 0 ||
                    !TryPath(face.transform, target.Root, out var path))
                { error = "얼굴 메시는 루트 하위의 BlendShape 메시여야 합니다."; return false; }
                if (!paths.Add(path)) { error = "동일한 경로의 얼굴 메시가 있습니다."; return false; }
            }
            foreach (var bone in new[] { target.Head, target.LeftEye, target.RightEye })
                if (bone != null && !TryPath(bone, target.Root, out _)) { error = "머리·눈 본은 캐릭터 루트 하위여야 합니다."; return false; }
            return true;
        }

        public static bool TryPath(Transform child, Transform root, out string path)
        {
            path = "";
            var parts = new List<string>();
            while (child != null && child != root)
            {
                // Unity animation paths cannot distinguish identically named siblings.
                if (child.name.Contains("/") || (child.parent != null && child.parent.Cast<Transform>().Count(t => t.name == child.name) > 1)) return false;
                parts.Add(child.name);
                child = child.parent;
            }
            if (child == null) return false;
            parts.Reverse(); path = string.Join("/", parts);
            return true;
        }

        public string TargetLabel(int index) => index >= 0 && index < Targets.Count && Targets[index] != null
            ? (!string.IsNullOrWhiteSpace(Targets[index].Label) ? Targets[index].Label : Targets[index].Root != null ? Targets[index].Root.name : "미지정") : "미선택";

        public bool ApplyPacket(string text)
        {
            if (!YamoFacialPacket.TryParse(text, out var packet)) return false;
            foreach (var face in faces)
            {
                if (face.Renderer == null || face.Renderer.sharedMesh != face.Mesh) continue;
                foreach (var weight in packet.Weights)
                {
                    var name = Mirror ? YamoFacialPacket.MirrorName(weight.Key) : weight.Key;
                    if (face.Indices.TryGetValue(name, out var index)) face.Renderer.SetBlendShapeWeight(index, weight.Value);
                }
            }
            if (ReceiveHeadAndEyes)
            {
                ApplyRotation(head, "head", packet, true);
                ApplyRotation(Mirror ? rightEye : leftEye, "leftEye", packet, false);
                ApplyRotation(Mirror ? leftEye : rightEye, "rightEye", packet, false);
            }
            lastPacketAt = Time.realtimeSinceStartup;
            return true;
        }

        private void ApplyRotation(Transform bone, string key, YamoFacialPacket packet, bool isHead)
        {
            if (bone == null || !packet.Rotations.TryGetValue(key, out var angles)) return;
            bone.localRotation = Quaternion.Euler(angles.x, Mirror ? -angles.y : angles.y, isHead ? -angles.z : angles.z);
        }

        public bool StartRecording()
        {
#if !UNITY_EDITOR
            Status = "애니메이션 에셋 녹화는 Unity Editor Play Mode에서 지원합니다.";
            return false;
#else
            if (!Application.isPlaying) { Status = "Play Mode에서 녹화하세요."; return false; }
            if (IsRecording) return true;
            if (take != null) { Status = "이전 녹화의 저장 재시도를 먼저 실행하세요."; return false; }
            if (ActiveTargetIndex < 0 || ActiveTargetIndex >= Targets.Count) { Status = "캐릭터를 먼저 선택하세요."; return false; }
            // Inspector assignments can be edited during Play Mode. Rebuild live bindings too.
            if (!SelectTarget(ActiveTargetIndex)) return false;
            var target = Targets[ActiveTargetIndex];
            if (!ValidateTarget(target, out var error)) { Status = error; return false; }
            var folder = (OutputFolder ?? "").Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal) || folder.Split('/').Any(p => p == ".." || p == "." || p.Length == 0))
            { Status = "저장 폴더는 Assets/ 하위 경로로 지정하세요."; return false; }
            var next = new Take { Label = TargetLabel(ActiveTargetIndex), Folder = folder, Rate = Mathf.Clamp(SampleRate, 1, 120), Mode = RecordingMode };
            var excluded = new HashSet<string>(ExcludedBlendShapes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var face in target.FaceMeshes.Distinct())
            {
                TryPath(face.transform, target.Root, out var path);
                for (var i = 0; i < face.sharedMesh.blendShapeCount; i++)
                {
                    var name = face.sharedMesh.GetBlendShapeName(i);
                    if (!excluded.Contains(name)) next.Channels.Add(new Channel { Target = face, Mesh = face.sharedMesh, Path = path, Property = "blendShape." + name, Index = i });
                }
            }
            if (RecordingMode == YamoFacialRecordingMode.FaceAndHeadEyes)
                foreach (var bone in new[] { target.Head, target.LeftEye, target.RightEye }.Where(t => t != null).Distinct())
                {
                    TryPath(bone, target.Root, out var path);
                    for (var i = 0; i < 4; i++) next.Channels.Add(new Channel { Target = bone, Path = path, Property = "m_LocalRotation." + "xyzw"[i], Index = i });
                }
            if (next.Channels.Count == 0) { Status = "녹화할 채널이 없습니다."; return false; }
            take = next;
            IsRecording = true;
            startTime = Time.realtimeSinceStartup;
            lastSample = -1f;
            Sample(0f);
            Status = "녹화 중: " + next.Label;
            return true;
#endif
        }

        private bool Sample(float time)
        {
            if (take == null || time <= lastSample) return true;
            foreach (var channel in take.Channels)
            {
                if (channel.Target == null || (channel.Target is SkinnedMeshRenderer renderer && renderer.sharedMesh != channel.Mesh)) return false;
            }
            foreach (var channel in take.Channels)
            {
                float value;
                if (channel.Target is SkinnedMeshRenderer face) value = face.GetBlendShapeWeight(channel.Index);
                else
                {
                    var q = ((Transform)channel.Target).localRotation;
                    value = channel.Index == 0 ? q.x : channel.Index == 1 ? q.y : channel.Index == 2 ? q.z : q.w;
                }
                channel.Curve.AddKey(time, value);
            }
            lastSample = time;
            return true;
        }

        public bool StopRecording()
        {
            if (IsRecording) Sample(Time.realtimeSinceStartup - startTime);
            IsRecording = false;
            return take == null || SaveTake();
        }

        public bool SaveTake()
        {
#if UNITY_EDITOR
            if (take == null) return true;
            if (IsRecording) { Status = "녹화 종료 후 저장하세요."; return false; }
            AnimationClip clip = null;
            try
            {
                if (!Directory.Exists(take.Folder)) Directory.CreateDirectory(take.Folder);
                var folderParts = take.Folder.Split('/');
                var folderPath = folderParts[0];
                for (var i = 1; i < folderParts.Length; i++)
                {
                    folderPath += "/" + folderParts[i];
                    if (!AssetDatabase.IsValidFolder(folderPath)) AssetDatabase.ImportAsset(folderPath);
                }
                var safeName = string.Concat(take.Label.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                clip = new AnimationClip { frameRate = take.Rate, name = safeName + "_Facial" };
                foreach (var channel in take.Channels)
                {
                    for (var i = 0; i < channel.Curve.length; i++)
                    {
                        AnimationUtility.SetKeyLeftTangentMode(channel.Curve, i, AnimationUtility.TangentMode.Linear);
                        AnimationUtility.SetKeyRightTangentMode(channel.Curve, i, AnimationUtility.TangentMode.Linear);
                    }
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(channel.Path,
                        channel.Property.StartsWith("blendShape.", StringComparison.Ordinal) ? typeof(SkinnedMeshRenderer) : typeof(Transform), channel.Property), channel.Curve);
                }
                clip.EnsureQuaternionContinuity();
                var path = AssetDatabase.GenerateUniqueAssetPath(take.Folder + "/" + safeName + "_Facial_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".anim");
                AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssets();
                LastSavedPath = path;
                Status = "저장됨: " + path;
                take = null;
                return true;
            }
            catch (Exception error)
            {
                if (clip != null && !EditorUtility.IsPersistent(clip)) DestroyImmediate(clip);
                Status = "저장 실패 (데이터 유지 · 저장 재시도): " + error.Message;
                Debug.LogError("[YAMO Facial] " + Status, this);
                return false;
            }
#else
            return false;
#endif
        }

        private void Update()
        {
            if (!EnableHotkeys) return;
            for (var i = 0; i < Mathf.Min(Targets.Count, 10); i++)
                if (KeyDown(i == 9 ? KeyCode.Keypad0 : KeyCode.Keypad1 + i)) SelectTarget(i);
            if (KeyDown(RecordKey)) StartRecording();
            if (KeyDown(StopKey)) StopRecording();
        }

        private void LateUpdate()
        {
            var current = session;
            if (current != null)
            {
                if (current.Error != null) { Status = "수신 오류: " + current.Error; Disconnect(); }
                else
                {
                    var packet = Interlocked.Exchange(ref current.Latest, null);
                    if (packet != null)
                    {
                        lastRawPacketAt = Time.realtimeSinceStartup;
                        LastPacketPreview = packet.Substring(0, Mathf.Min(200, packet.Length));
                        if (ApplyPacket(packet)) ParsedPackets++;
                        else Status = "UDP 데이터는 수신했지만 지원하는 페이셜 텍스트 형식이 아닙니다.";
                    }
                    if (Time.realtimeSinceStartup - lastRawPacketAt > 2f && Time.realtimeSinceStartup - lastConnectRequest >= 3f)
                        RequestStream();
                }
            }
            if (!IsRecording) return;
            var elapsed = Time.realtimeSinceStartup - startTime;
            if (elapsed - lastSample >= 1f / take.Rate && !Sample(elapsed))
            {
                StopRecording();
                Status = "캐릭터 또는 메시 변경으로 녹화를 종료했습니다. " + Status;
            }
        }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        private static readonly Dictionary<KeyCode, PropertyInfo> NewInputKeys = new Dictionary<KeyCode, PropertyInfo>();
        private static Type keyboardType;
#endif
        private static bool KeyDown(KeyCode key)
        {
            if (key == KeyCode.None) return false;
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(key);
#elif ENABLE_INPUT_SYSTEM
            // Optional integration: do not force the Input System package on Unity 2021 projects.
            keyboardType = keyboardType ?? Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            if (keyboardType == null) return false;
            var keyboard = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (keyboard == null) return false;
            if (!NewInputKeys.TryGetValue(key, out var property))
            {
                var name = key.ToString();
                if (name.StartsWith("Keypad", StringComparison.Ordinal)) name = "numpad" + name.Substring(6);
                else if (name.StartsWith("Alpha", StringComparison.Ordinal)) name = "digit" + name.Substring(5);
                else name = char.ToLowerInvariant(name[0]) + name.Substring(1);
                switch (key)
                {
                    case KeyCode.Return: name = "enter"; break;
                    case KeyCode.LeftControl: name = "leftCtrl"; break;
                    case KeyCode.RightControl: name = "rightCtrl"; break;
                    case KeyCode.Numlock: name = "numLock"; break;
                    case KeyCode.Print: name = "printScreen"; break;
                    case KeyCode.LeftCommand: case KeyCode.LeftWindows: name = "leftMeta"; break;
                    case KeyCode.RightCommand: case KeyCode.RightWindows: name = "rightMeta"; break;
                }
                property = keyboardType.GetProperty(name + "Key");
                NewInputKeys[key] = property;
            }
            var control = property?.GetValue(keyboard);
            return control != null && (bool)control.GetType().GetProperty("wasPressedThisFrame").GetValue(control);
#else
            return false;
#endif
        }

        private void OnGUI()
        {
            if (!ShowOverlay || !Application.isPlaying) return;
            var oldColor = GUI.color;
            var oldMatrix = GUI.matrix;
            var scale = Mathf.Clamp(OverlayScale, .5f, 3f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var area = new Rect(12, 12, Mathf.Max(100, Mathf.Min(440, Screen.width / scale - 24)), Mathf.Min(390, Screen.height / scale - 24));
            GUI.color = new Color(.07f, .07f, .09f, .9f);
            GUI.DrawTexture(area, Texture2D.whiteTexture);
            GUI.color = oldColor;
            headerStyle = headerStyle ?? new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            statusStyle = statusStyle ?? new GUIStyle(GUI.skin.label) { wordWrap = true };
            GUILayout.BeginArea(area, GUI.skin.box);
            GUI.color = IsRecording ? new Color(1f, .35f, .35f) : Color.white;
            GUILayout.Label(IsRecording ? "● REC  " + RecordingSeconds.ToString("F1") + " s  ·  " + (take != null ? take.Label : "") : "YAMO Facial Recorder · 대기", headerStyle);
            GUI.color = oldColor;
            GUILayout.Label("대상: " + TargetLabel(ActiveTargetIndex) + "  |  " + (HasRecentPacket ? "수신 중" : IsReceiving ? "데이터 대기" : "연결 안 됨"));
            GUILayout.Label("UDP " + Port + " · 패킷 " + ReceivedPackets + " · 해석 " + ParsedPackets + (string.IsNullOrEmpty(LastSender) ? "" : " · " + LastSender));
            var mode = IsRecording && take != null ? take.Mode : RecordingMode;
            GUILayout.Label("모드: " + (mode == YamoFacialRecordingMode.FaceOnly ? "페이셜만 (BlendShape)" : "페이셜 + 머리·눈") +
                (EnableHotkeys ? "   ·   " + RecordKey + " 녹화 / " + StopKey + " 종료" : "   ·   단축키 꺼짐"));
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Min(170, Targets.Count * 26 + 8)));
            for (var i = 0; i < Targets.Count; i++)
                if (GUILayout.Button((i < 10 ? "Num " + (i == 9 ? 0 : i + 1) + "  " : "") + (i == ActiveTargetIndex ? "▶ " : "") + TargetLabel(i))) SelectTarget(i);
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(IsRecording ? "녹화 종료 / 저장" : "녹화 시작")) { if (IsRecording) StopRecording(); else StartRecording(); }
            GUILayout.EndHorizontal();
            if (HasUnsavedTake && GUILayout.Button("저장 재시도")) SaveTake();
            GUILayout.Label(Status, statusStyle);
            GUILayout.EndArea();
            GUI.color = oldColor;
            GUI.matrix = oldMatrix;
        }
    }
}
