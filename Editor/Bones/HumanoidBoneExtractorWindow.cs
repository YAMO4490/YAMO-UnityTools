using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public class HumanoidBoneExtractorWindow : EditorWindow
    {
        [SerializeField] private GameObject _sourceObject;
        [SerializeField] private bool _includeFingers = true;
        [SerializeField] private bool _stripComponents = true;
        [SerializeField] private bool _showMapping;
        private Vector2 _scrollPos;

        private readonly Dictionary<HumanBodyBones, Transform> _boneMap =
            new Dictionary<HumanBodyBones, Transform>();
        private bool _dirty = true;

        // ────────── Bone categories ──────────

        private static readonly HumanBodyBones[] BodyBones =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.Neck,
            HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
            HumanBodyBones.RightToes,
            HumanBodyBones.LeftEye,
            HumanBodyBones.RightEye,
            HumanBodyBones.Jaw,
        };

        private static readonly HumanBodyBones[] LeftFingerBones =
        {
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
            HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
            HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
            HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
        };

        private static readonly HumanBodyBones[] RightFingerBones =
        {
            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
            HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
            HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
            HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
        };

        private static readonly HumanBodyBones[] AllFingerBones =
            LeftFingerBones.Concat(RightFingerBones).ToArray();

        private static readonly HashSet<HumanBodyBones> Required = new HashSet<HumanBodyBones>
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        // 자동 감지에서 제외 — 필요 시 수동 매핑.
        private static readonly HashSet<HumanBodyBones> AutoDetectExcluded = new HashSet<HumanBodyBones>
        {
            HumanBodyBones.UpperChest,
            HumanBodyBones.LeftEye,
            HumanBodyBones.RightEye,
            HumanBodyBones.Jaw,
        };

        // ════════════════════════════════════════════════════════════════
        // Window
        // ════════════════════════════════════════════════════════════════

        [MenuItem("Tools/YAMO/Bones/Humanoid Bone Extractor")]
        public static void Open()
        {
            if (HasOpenInstances<HumanoidBoneExtractorWindow>())
                GetWindow<HumanoidBoneExtractorWindow>().Close();
            else
            {
                var w = GetWindow<HumanoidBoneExtractorWindow>("Bone Extractor");
                w.minSize = new Vector2(420, 520);
            }
        }

        private void OnGUI() => DrawGUI();

        /// <summary>
        /// 외부(예: YamoAssetChecker / Tool Hub)에서 호출해 임베드할 수 있는 GUI 본체.
        /// </summary>
        public void DrawGUI()
        {
            EditorGUILayout.HelpBox(
                "아바타를 복제 후 휴머노이드 본만 남깁니다.\n" +
                "Animator 생성용 스켈레톤 추출에 사용합니다.",
                MessageType.Info);
            EditorGUILayout.Space();

            // ── Source ──
            EditorGUI.BeginChangeCheck();
            _sourceObject = EditorGUILayout.ObjectField(
                "소스 아바타", _sourceObject, typeof(GameObject), true) as GameObject;
            if (EditorGUI.EndChangeCheck())
                _dirty = true;

            if (_sourceObject == null)
            {
                EditorGUILayout.HelpBox(
                    "Hierarchy에서 아바타 GameObject를 지정하세요.", MessageType.Warning);
                return;
            }

            if (_dirty) { DetectBones(); _dirty = false; }

            // ── Status ──
            int mapped = _boneMap.Count(kv => kv.Value != null);
            int reqMapped = Required.Count(b => _boneMap.TryGetValue(b, out var t) && t != null);

            EditorGUILayout.Space();
            var anim = _sourceObject.GetComponentInChildren<Animator>();
            bool isHuman = anim != null && anim.avatar != null && anim.avatar.isHuman;

            if (isHuman)
                EditorGUILayout.HelpBox(
                    $"Humanoid Avatar 감지 — {mapped}개 본 매핑됨", MessageType.Info);
            else
                EditorGUILayout.HelpBox(
                    $"Humanoid 미감지 — 이름 기반 추측 ({mapped}개 매핑)\n" +
                    $"필수 본: {reqMapped}/{Required.Count}",
                    reqMapped >= Required.Count ? MessageType.Warning : MessageType.Error);

            // ── Options ──
            EditorGUILayout.Space();
            _includeFingers = EditorGUILayout.Toggle("손가락 본 포함", _includeFingers);
            _stripComponents = EditorGUILayout.Toggle("컴포넌트 제거", _stripComponents);

            // ── Mapping ──
            EditorGUILayout.Space();
            _showMapping = EditorGUILayout.Foldout(_showMapping, $"본 매핑 ({mapped}개)", true);
            if (_showMapping) DrawMappingUI();

            // ── Actions ──
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("다시 감지", GUILayout.Height(28)))
                _dirty = true;

            GUI.enabled = mapped > 0;
            if (GUILayout.Button("복제 & 추출", GUILayout.Height(28)))
                Execute();
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            // ── Warnings ──
            if (reqMapped < Required.Count)
            {
                var missing = Required.Where(b => !_boneMap.TryGetValue(b, out var t) || t == null);
                EditorGUILayout.HelpBox(
                    "누락된 필수 본: " + string.Join(", ", missing), MessageType.Warning);
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Detection
        // ════════════════════════════════════════════════════════════════

        private void DetectBones()
        {
            _boneMap.Clear();
            if (_sourceObject == null) return;

            var anim = _sourceObject.GetComponentInChildren<Animator>();
            if (anim != null && anim.avatar != null && anim.avatar.isHuman)
                DetectFromAnimator(anim);
            else
                GuessByName(_sourceObject.transform);
        }

        private void DetectFromAnimator(Animator anim)
        {
            foreach (var b in BodyBones)
            {
                if (AutoDetectExcluded.Contains(b)) continue;
                var t = anim.GetBoneTransform(b);
                if (t != null) _boneMap[b] = t;
            }
            foreach (var b in AllFingerBones)
            {
                var t = anim.GetBoneTransform(b);
                if (t != null) _boneMap[b] = t;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Name-based guesser (hierarchical)
        // ════════════════════════════════════════════════════════════════

        private void GuessByName(Transform root)
        {
            // Hips
            var hips = FindDescendant(root, @"hip|pelvis", 4);
            if (hips == null) return;
            Set(HumanBodyBones.Hips, hips);

            // Spine chain
            var spine = SearchChild(hips, @"spine");
            Set(HumanBodyBones.Spine, spine);

            var chest = SearchChild(spine, @"chest|spine[._\s]?1");
            Set(HumanBodyBones.Chest, chest);

            // UpperChest는 자동 매핑하지 않음. chain 탐색용 로컬 변수로만 사용.
            var upperChest = SearchChild(chest, @"upper.*chest|spine[._\s]?2");

            var trunkTop = upperChest ?? chest;

            var neck = SearchChild(trunkTop, @"neck");
            Set(HumanBodyBones.Neck, neck);

            var head = SearchChild(neck, @"head");
            Set(HumanBodyBones.Head, head);

            // Eye/Jaw는 자동 매핑하지 않음. 필요 시 수동 매핑.

            // Shoulders
            if (trunkTop != null)
                FindLRPair(trunkTop, @"shoulder|clavicle",
                    HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder);

            // Arms
            GuessArmChain(trunkTop, true);
            GuessArmChain(trunkTop, false);

            // Legs
            FindLRPair(hips, @"leg|thigh",
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg);
            GuessLegChain(true);
            GuessLegChain(false);

            // Fingers
            GuessFingers(true);
            GuessFingers(false);
        }

        private void GuessArmChain(Transform trunkTop, bool left)
        {
            var shoulderKey = left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder;
            var upperKey    = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
            var lowerKey    = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
            var handKey     = left ? HumanBodyBones.LeftHand     : HumanBodyBones.RightHand;

            Transform parent = Get(shoulderKey) ?? trunkTop;
            if (parent == null) return;

            var ua = SearchChildLR(parent, @"upper.*arm|arm", left);
            Set(upperKey, ua);

            var la = SearchChild(ua, @"lower.*arm|fore.*arm|elbow");
            if (la == null && ua != null && ua.childCount == 1) la = ua.GetChild(0);
            Set(lowerKey, la);

            var h = SearchChild(la, @"hand|wrist");
            if (h == null && la != null && la.childCount == 1) h = la.GetChild(0);
            Set(handKey, h);
        }

        private void GuessLegChain(bool left)
        {
            var upperKey = left ? HumanBodyBones.LeftUpperLeg  : HumanBodyBones.RightUpperLeg;
            var lowerKey = left ? HumanBodyBones.LeftLowerLeg  : HumanBodyBones.RightLowerLeg;
            var footKey  = left ? HumanBodyBones.LeftFoot      : HumanBodyBones.RightFoot;
            var toesKey  = left ? HumanBodyBones.LeftToes      : HumanBodyBones.RightToes;

            var ul = Get(upperKey);
            if (ul == null) return;

            var ll = SearchChild(ul, @"lower.*leg|calf|knee|shin");
            if (ll == null && ul.childCount == 1) ll = ul.GetChild(0);
            Set(lowerKey, ll);

            var ft = SearchChild(ll, @"foot|ankle");
            if (ft == null && ll != null && ll.childCount == 1) ft = ll.GetChild(0);
            Set(footKey, ft);

            Set(toesKey, SearchChild(ft, @"toe"));
        }

        private void GuessFingers(bool left)
        {
            var h = Get(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (h == null) return;

            GuessFingerChain(h, @"thumb|finger[._\s]?0\b",
                left ? HumanBodyBones.LeftThumbProximal   : HumanBodyBones.RightThumbProximal,
                left ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate,
                left ? HumanBodyBones.LeftThumbDistal     : HumanBodyBones.RightThumbDistal);

            GuessFingerChain(h, @"index|finger[._\s]?1(?!\d)",
                left ? HumanBodyBones.LeftIndexProximal   : HumanBodyBones.RightIndexProximal,
                left ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate,
                left ? HumanBodyBones.LeftIndexDistal     : HumanBodyBones.RightIndexDistal);

            GuessFingerChain(h, @"mid(dle)?|finger[._\s]?2(?!\d)",
                left ? HumanBodyBones.LeftMiddleProximal   : HumanBodyBones.RightMiddleProximal,
                left ? HumanBodyBones.LeftMiddleIntermediate : HumanBodyBones.RightMiddleIntermediate,
                left ? HumanBodyBones.LeftMiddleDistal     : HumanBodyBones.RightMiddleDistal);

            GuessFingerChain(h, @"ring|finger[._\s]?3(?!\d)",
                left ? HumanBodyBones.LeftRingProximal   : HumanBodyBones.RightRingProximal,
                left ? HumanBodyBones.LeftRingIntermediate : HumanBodyBones.RightRingIntermediate,
                left ? HumanBodyBones.LeftRingDistal     : HumanBodyBones.RightRingDistal);

            GuessFingerChain(h, @"little|pinky|small|finger[._\s]?4(?!\d)",
                left ? HumanBodyBones.LeftLittleProximal   : HumanBodyBones.RightLittleProximal,
                left ? HumanBodyBones.LeftLittleIntermediate : HumanBodyBones.RightLittleIntermediate,
                left ? HumanBodyBones.LeftLittleDistal     : HumanBodyBones.RightLittleDistal);
        }

        private void GuessFingerChain(Transform hand, string pattern,
            HumanBodyBones proximal, HumanBodyBones intermediate, HumanBodyBones distal)
        {
            Transform prox = null;
            foreach (Transform child in hand)
            {
                if (Regex.IsMatch(child.name, pattern, RegexOptions.IgnoreCase))
                { prox = child; break; }
            }
            if (prox == null) return;
            Set(proximal, prox);

            if (prox.childCount > 0)
            {
                var inter = prox.GetChild(0);
                Set(intermediate, inter);
                if (inter.childCount > 0)
                    Set(distal, inter.GetChild(0));
            }
        }

        // ── Search helpers ──

        private void Set(HumanBodyBones bone, Transform t)
        {
            if (t != null) _boneMap[bone] = t;
        }

        private Transform Get(HumanBodyBones bone)
        {
            return _boneMap.TryGetValue(bone, out var t) ? t : null;
        }

        private static Transform FindDescendant(Transform root, string pat, int depth)
        {
            if (depth <= 0) return null;
            foreach (Transform c in root)
                if (Regex.IsMatch(c.name, pat, RegexOptions.IgnoreCase)) return c;
            foreach (Transform c in root)
            {
                var f = FindDescendant(c, pat, depth - 1);
                if (f != null) return f;
            }
            return null;
        }

        private static Transform SearchChild(Transform parent, string pat)
        {
            if (parent == null) return null;
            foreach (Transform c in parent)
                if (Regex.IsMatch(c.name, pat, RegexOptions.IgnoreCase)) return c;
            foreach (Transform c in parent)
                foreach (Transform gc in c)
                    if (Regex.IsMatch(gc.name, pat, RegexOptions.IgnoreCase)) return gc;
            return null;
        }

        private static Transform SearchChildLR(Transform parent, string pat, bool left)
        {
            if (parent == null) return null;
            var candidates = new List<Transform>();
            foreach (Transform c in parent)
                if (Regex.IsMatch(c.name, pat, RegexOptions.IgnoreCase))
                    candidates.Add(c);

            foreach (var c in candidates)
                if (left ? IsLeftName(c.name) : IsRightName(c.name)) return c;

            if (candidates.Count >= 2)
            {
                candidates.Sort((a, b) => a.position.x.CompareTo(b.position.x));
                return left ? candidates[0] : candidates[candidates.Count - 1];
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private void FindLRPair(Transform parent, string pat,
            HumanBodyBones leftBone, HumanBodyBones rightBone)
        {
            if (parent == null) return;
            var candidates = new List<Transform>();
            foreach (Transform c in parent)
                if (Regex.IsMatch(c.name, pat, RegexOptions.IgnoreCase))
                    candidates.Add(c);
            if (candidates.Count == 0) return;

            Transform lt = null, rt = null;
            foreach (var c in candidates)
            {
                if (lt == null && IsLeftName(c.name)) lt = c;
                else if (rt == null && IsRightName(c.name)) rt = c;
            }

            if (lt == null && rt == null && candidates.Count >= 2)
            {
                candidates.Sort((a, b) => a.position.x.CompareTo(b.position.x));
                lt = candidates[0];
                rt = candidates[candidates.Count - 1];
            }
            else if (lt == null && rt == null && candidates.Count == 1)
            {
                if (candidates[0].position.x < 0) lt = candidates[0];
                else rt = candidates[0];
            }

            Set(leftBone, lt);
            Set(rightBone, rt);
        }

        private static bool IsLeftName(string n)
        {
            return n.ToLowerInvariant().Contains("left") ||
                   Regex.IsMatch(n, @"(^|[\s._\-:])[lL]([\s._\-:]|$)") ||
                   Regex.IsMatch(n, @"[._\-][lL]$");
        }

        private static bool IsRightName(string n)
        {
            return n.ToLowerInvariant().Contains("right") ||
                   Regex.IsMatch(n, @"(^|[\s._\-:])[rR]([\s._\-:]|$)") ||
                   Regex.IsMatch(n, @"[._\-][rR]$");
        }

        // ════════════════════════════════════════════════════════════════
        // Mapping UI
        // ════════════════════════════════════════════════════════════════

        private void DrawMappingUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.Height(300));

            DrawBoneSection("Body", BodyBones);
            if (_includeFingers)
            {
                EditorGUILayout.Space();
                DrawBoneSection("Left Fingers", LeftFingerBones);
                EditorGUILayout.Space();
                DrawBoneSection("Right Fingers", RightFingerBones);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawBoneSection(string header, HumanBodyBones[] bones)
        {
            EditorGUILayout.LabelField(header, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            foreach (var bone in bones)
            {
                _boneMap.TryGetValue(bone, out var cur);
                bool req = Required.Contains(bone);

                EditorGUILayout.BeginHorizontal();

                if (cur == null && req)
                {
                    var c = GUI.color;
                    GUI.color = new Color(1f, 0.6f, 0.6f);
                    EditorGUILayout.LabelField(bone.ToString(), GUILayout.Width(190));
                    GUI.color = c;
                }
                else
                {
                    EditorGUILayout.LabelField(bone.ToString(), GUILayout.Width(190));
                }

                var next = EditorGUILayout.ObjectField(cur, typeof(Transform), true) as Transform;
                if (next != cur)
                {
                    if (next != null) _boneMap[bone] = next;
                    else _boneMap.Remove(bone);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;
        }

        // ════════════════════════════════════════════════════════════════
        // Execute
        // ════════════════════════════════════════════════════════════════

        private void Execute()
        {
            var activeBones = new HashSet<Transform>();
            foreach (var kv in _boneMap)
            {
                if (kv.Value == null) continue;
                if (!_includeFingers && AllFingerBones.Contains(kv.Key)) continue;
                activeBones.Add(kv.Value);
            }

            if (activeBones.Count == 0)
            {
                EditorUtility.DisplayDialog("오류", "추출할 본이 없습니다.", "확인");
                return;
            }

            var clone = Instantiate(_sourceObject);
            clone.name = _sourceObject.name + "_Avatar";
            Undo.RegisterCreatedObjectUndo(clone, "Humanoid Bone Extract");

            var srcRoot = _sourceObject.transform;
            var dstRoot = clone.transform;
            var keepSet = new HashSet<Transform> { dstRoot };

            foreach (var srcBone in activeBones)
            {
                var path = RelativePath(srcRoot, srcBone);
                if (path == null) continue;
                var dst = path.Length == 0 ? dstRoot : dstRoot.Find(path);
                if (dst == null) continue;

                for (var t = dst; t != null; t = t.parent)
                {
                    keepSet.Add(t);
                    if (t == dstRoot) break;
                }
            }

            // deepest-first deletion
            var toDestroy = dstRoot.GetComponentsInChildren<Transform>(true)
                .Where(t => t != null && t != dstRoot && !keepSet.Contains(t))
                .OrderByDescending(t => Depth(t, dstRoot))
                .ToList();

            foreach (var t in toDestroy)
                if (t != null) DestroyImmediate(t.gameObject);

            if (_stripComponents)
            {
                foreach (var t in dstRoot.GetComponentsInChildren<Transform>(true))
                    foreach (var comp in t.GetComponents<Component>())
                        if (!(comp is Transform)) DestroyImmediate(comp);
            }

            Selection.activeGameObject = clone;

            EditorUtility.DisplayDialog("완료",
                $"'{clone.name}' 생성 완료\n" +
                $"유지된 본: {keepSet.Count}개\n" +
                $"제거된 오브젝트: {toDestroy.Count}개",
                "확인");
        }

        private static string RelativePath(Transform root, Transform target)
        {
            if (target == root) return "";
            var parts = new List<string>();
            for (var t = target; t != null && t != root; t = t.parent)
                parts.Add(t.name);
            var check = target;
            while (check != null && check != root) check = check.parent;
            if (check != root) return null;
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static int Depth(Transform t, Transform root)
        {
            int d = 0;
            while (t != null && t != root) { d++; t = t.parent; }
            return d;
        }
    }
}
