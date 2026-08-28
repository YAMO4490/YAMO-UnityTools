using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    public sealed class BlendShapeCurveRemapperWindow : EditorWindow
    {
        private const string MenuPath = "Tools/YAMO/Animation/BlendShape Curve Remapper";
        private const string PreferencePrefix = "YAMO.UnityTools.BlendShapeCurveRemapper.";
        private const string DefaultCopySuffix = "_BlendShapeRemapped";

        private AnimationClip _sourceClip;
        private BlendShapeRemapSettings _settings;
        private string _copySuffix;
        private string _outputAssetPath;
        private bool _overwriteSource;
        private string _preferredMeshPath;
        private int _selectedMeshIndex = -1;
        private Vector2 _scrollPosition;

        private readonly List<BlendShapeMeshTrack> _meshTracks =
            new List<BlendShapeMeshTrack>();
        private readonly HashSet<string> _selectedPropertyNames =
            new HashSet<string>(StringComparer.Ordinal);

        private BlendShapeRemapReport _analysis;
        private string _analysisError;

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            var window = GetWindow<BlendShapeCurveRemapperWindow>("BlendShape Remapper");
            window.minSize = new Vector2(500f, 650f);
            window.Show();
        }

        private void OnEnable()
        {
            LoadPreferences();

            if (_sourceClip == null)
                _sourceClip = Selection.activeObject as AnimationClip;

            RefreshMeshTracks(false);
            SuggestOutputPath();
            RefreshAnalysis();
        }

        private void OnDisable()
        {
            SavePreferences();
        }

        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawHeader();
            DrawSourceSection();
            DrawTargetSection();
            DrawValueSection();
            DrawAnalysisSection();
            DrawOutputSection();
            DrawApplySection();
            EditorGUILayout.EndScrollView();
        }

        private static void DrawHeader()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("BlendShape Curve Remapper", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Select a Mesh track and one or more blend-shape properties from an AnimationClip. Every processed key is forced to Linear tangents.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(6f);
        }

        private void DrawSourceSection()
        {
            EditorGUILayout.LabelField("1. AnimationClip", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            AnimationClip nextClip = (AnimationClip)EditorGUILayout.ObjectField(
                "Source Clip",
                _sourceClip,
                typeof(AnimationClip),
                false);
            if (EditorGUI.EndChangeCheck())
            {
                _sourceClip = nextClip;
                _selectedPropertyNames.Clear();
                RefreshMeshTracks(false);
                SuggestOutputPath();
                RefreshAnalysis();
            }

            if (_sourceClip != null)
            {
                EditorGUILayout.LabelField(
                    "Discovered SkinnedMeshRenderer tracks",
                    _meshTracks.Count.ToString());
            }

            EditorGUILayout.Space(8f);
        }

        private void DrawTargetSection()
        {
            EditorGUILayout.LabelField("2. Mesh and BlendShape Selection", EditorStyles.boldLabel);

            if (_sourceClip == null)
            {
                EditorGUILayout.HelpBox("Assign an AnimationClip first.", MessageType.Info);
                EditorGUILayout.Space(8f);
                return;
            }

            if (_meshTracks.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No SkinnedMeshRenderer blendShape.* curves were found in this clip.",
                    MessageType.Warning);
                EditorGUILayout.Space(8f);
                return;
            }

            string[] meshOptions = BuildMeshOptions();
            EditorGUI.BeginChangeCheck();
            int nextMeshIndex = EditorGUILayout.Popup(
                new GUIContent(
                    "Mesh Track",
                    "This is the Transform path stored in the clip binding. Identical property names remain separated by path."),
                Mathf.Clamp(_selectedMeshIndex, 0, _meshTracks.Count - 1),
                meshOptions);
            if (EditorGUI.EndChangeCheck())
            {
                _selectedMeshIndex = nextMeshIndex;
                _preferredMeshPath = CurrentMeshTrack.MeshPath;
                _selectedPropertyNames.Clear();
                RefreshAnalysis();
            }

            BlendShapeMeshTrack track = CurrentMeshTrack;
            EditorGUILayout.HelpBox(
                "Selected Mesh binding path: " + FormatMeshPath(track.MeshPath),
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Select All", GUILayout.Width(80f)))
            {
                _selectedPropertyNames.Clear();
                foreach (string propertyName in track.PropertyNames)
                    _selectedPropertyNames.Add(propertyName);
                RefreshAnalysis();
            }

            if (GUILayout.Button("Clear All", GUILayout.Width(80f)))
            {
                _selectedPropertyNames.Clear();
                RefreshAnalysis();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foreach (string propertyName in track.PropertyNames)
            {
                bool selected = _selectedPropertyNames.Contains(propertyName);
                bool nextSelected = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        GetFriendlyPropertyName(propertyName),
                        track.MeshPath + " / " + propertyName),
                    selected);

                if (nextSelected == selected)
                    continue;

                if (nextSelected)
                    _selectedPropertyNames.Add(propertyName);
                else
                    _selectedPropertyNames.Remove(propertyName);

                RefreshAnalysis();
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.LabelField(
                "Selected properties",
                _selectedPropertyNames.Count.ToString());
            EditorGUILayout.Space(8f);
        }

        private void DrawValueSection()
        {
            EditorGUILayout.LabelField("3. Value Mapping", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            _settings.LowerThreshold = EditorGUILayout.FloatField(
                new GUIContent("Lower Threshold", "Values at or below this become Lower Output."),
                _settings.LowerThreshold);
            _settings.UpperThreshold = EditorGUILayout.FloatField(
                new GUIContent("Upper Threshold", "Values at or above this become Upper Output."),
                _settings.UpperThreshold);
            _settings.LowerOutput = EditorGUILayout.FloatField(
                "Lower Output",
                _settings.LowerOutput);
            _settings.MiddleMaximumOutput = EditorGUILayout.FloatField(
                new GUIContent(
                    "Middle Maximum Output",
                    "Values between the thresholds are compressed from Lower Output to this value."),
                _settings.MiddleMaximumOutput);
            _settings.UpperOutput = EditorGUILayout.FloatField(
                "Upper Output",
                _settings.UpperOutput);

            if (EditorGUI.EndChangeCheck())
                RefreshAnalysis();

            string settingsError = BlendShapeCurveRemapper.GetSettingsValidationError(_settings);
            if (!string.IsNullOrEmpty(settingsError))
            {
                EditorGUILayout.HelpBox(settingsError, MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox(BuildMappingDescription(), MessageType.None);
            }

            EditorGUILayout.HelpBox(
                "All processed keys use unweighted Linear tangents.",
                MessageType.Info);
            EditorGUILayout.Space(8f);
        }

        private void DrawAnalysisSection()
        {
            EditorGUILayout.LabelField("4. Analysis", EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(_analysisError))
            {
                EditorGUILayout.HelpBox(_analysisError, MessageType.Error);
            }
            else if (_sourceClip == null || _meshTracks.Count == 0)
            {
                EditorGUILayout.LabelField("There is no target to analyze.");
            }
            else if (_selectedPropertyNames.Count == 0)
            {
                EditorGUILayout.HelpBox("Select at least one blend-shape property.", MessageType.Info);
            }
            else if (_analysis == null || _analysis.MatchedCurveCount == 0)
            {
                EditorGUILayout.HelpBox("The selected curves were not found.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "Matched curves / keys",
                    string.Format("{0} / {1:N0}", _analysis.MatchedCurveCount, _analysis.TotalKeyCount));
                EditorGUILayout.LabelField(
                    "Keys whose values will change",
                    _analysis.ChangedKeyCount.ToString("N0"));

                foreach (BlendShapeCurveRemapEntry entry in _analysis.Curves)
                {
                    EditorGUILayout.LabelField(
                        "  " + GetFriendlyPropertyName(entry.PropertyName),
                        string.Format(
                            "{0:N0} keys, {1:0.###}..{2:0.###} -> {3:0.###}..{4:0.###}",
                            entry.KeyCount,
                            entry.OriginalMinimum,
                            entry.OriginalMaximum,
                            entry.OutputMinimum,
                            entry.OutputMaximum));
                }
            }

            EditorGUILayout.Space(8f);
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("5. Output", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _overwriteSource = EditorGUILayout.ToggleLeft(
                "Overwrite source .anim (register Undo)",
                _overwriteSource);
            if (EditorGUI.EndChangeCheck())
                SavePreferences();

            if (_overwriteSource)
            {
                string overwriteError = BlendShapeCurveRemapper.GetOverwriteValidationError(_sourceClip);
                if (!string.IsNullOrEmpty(overwriteError))
                    EditorGUILayout.HelpBox(overwriteError, MessageType.Error);
                else
                    EditorGUILayout.HelpBox("The source asset will be modified directly.", MessageType.Warning);
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                _copySuffix = EditorGUILayout.TextField("Copy Suffix", _copySuffix);
                if (EditorGUI.EndChangeCheck())
                    SuggestOutputPath();

                EditorGUILayout.BeginHorizontal();
                _outputAssetPath = EditorGUILayout.TextField("Output Asset", _outputAssetPath);
                if (GUILayout.Button("Browse", GUILayout.Width(55f)))
                    BrowseOutputPath();
                EditorGUILayout.EndHorizontal();

                string copyError = GetCopyValidationError();
                if (!string.IsNullOrEmpty(copyError))
                    EditorGUILayout.HelpBox(copyError, MessageType.Error);
            }

            EditorGUILayout.HelpBox(
                "This mapping is not idempotent. Applying it repeatedly compresses middle values again.",
                MessageType.Warning);
            EditorGUILayout.Space(8f);
        }

        private void DrawApplySection()
        {
            string validationError = GetApplyValidationError();
            using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(validationError)))
            {
                string label = _overwriteSource
                    ? "Apply Selected Curves to Source"
                    : "Create Processed Copy";
                if (GUILayout.Button(label, GUILayout.Height(34f)))
                    ApplyRemap();
            }

            if (!string.IsNullOrEmpty(validationError))
                EditorGUILayout.LabelField(validationError, EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reset Defaults", GUILayout.Width(100f)))
                ResetDefaults();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(8f);
        }

        private void ApplyRemap()
        {
            if (_overwriteSource &&
                !EditorUtility.DisplayDialog(
                    "BlendShape Remapper",
                    "Apply the selected Mesh blend-shape curves directly to the source .anim?",
                    "Apply",
                    "Cancel"))
            {
                return;
            }

            try
            {
                BlendShapeRemapReport report;
                if (_overwriteSource)
                {
                    report = BlendShapeCurveRemapper.OverwriteAsset(
                        _sourceClip,
                        _settings,
                        BuildTargets());
                    RefreshMeshTracks(true);
                    RefreshAnalysis();
                }
                else
                {
                    AnimationClip resultClip = BlendShapeCurveRemapper.CreateProcessedCopyAsset(
                        _sourceClip,
                        _outputAssetPath,
                        _settings,
                        BuildTargets(),
                        out report);
                    Selection.activeObject = resultClip;
                    EditorGUIUtility.PingObject(resultClip);
                    SuggestOutputPath();
                }

                EditorUtility.DisplayDialog(
                    "BlendShape Remapper Complete",
                    string.Format(
                        "Processed {0} curves and {1:N0} keys.\nChanged values: {2:N0}\nTarget tangents: Linear",
                        report.MatchedCurveCount,
                        report.TotalKeyCount,
                        report.ChangedKeyCount),
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("BlendShape Remapper Error", exception.Message, "OK");
            }
        }

        private void RefreshMeshTracks(bool preservePropertySelection)
        {
            string previousPath = CurrentMeshTrack != null
                ? CurrentMeshTrack.MeshPath
                : _preferredMeshPath;

            _meshTracks.Clear();
            _selectedMeshIndex = -1;

            if (_sourceClip == null)
            {
                _selectedPropertyNames.Clear();
                return;
            }

            _meshTracks.AddRange(BlendShapeCurveRemapper.DiscoverMeshTracks(_sourceClip));
            if (_meshTracks.Count == 0)
            {
                _selectedPropertyNames.Clear();
                return;
            }

            _selectedMeshIndex = FindMeshIndex(previousPath);
            if (_selectedMeshIndex < 0)
                _selectedMeshIndex = 0;

            _preferredMeshPath = CurrentMeshTrack.MeshPath;

            if (!preservePropertySelection)
            {
                _selectedPropertyNames.Clear();
                return;
            }

            var availableProperties = new HashSet<string>(
                CurrentMeshTrack.PropertyNames,
                StringComparer.Ordinal);
            _selectedPropertyNames.RemoveWhere(
                propertyName => !availableProperties.Contains(propertyName));
        }

        private void RefreshAnalysis()
        {
            _analysis = null;
            _analysisError = null;

            if (_sourceClip == null || _meshTracks.Count == 0 ||
                _selectedMeshIndex < 0 || _selectedPropertyNames.Count == 0)
            {
                Repaint();
                return;
            }

            try
            {
                _analysis = BlendShapeCurveRemapper.AnalyzeClip(
                    _sourceClip,
                    _settings,
                    BuildTargets());
            }
            catch (Exception exception)
            {
                _analysisError = exception.Message;
            }

            Repaint();
        }

        private List<BlendShapeCurveTarget> BuildTargets()
        {
            var targets = new List<BlendShapeCurveTarget>(_selectedPropertyNames.Count);
            BlendShapeMeshTrack track = CurrentMeshTrack;
            if (track == null)
                return targets;

            foreach (string propertyName in _selectedPropertyNames)
                targets.Add(new BlendShapeCurveTarget(track.MeshPath, propertyName));

            targets.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.PropertyName, right.PropertyName));
            return targets;
        }

        private string[] BuildMeshOptions()
        {
            var options = new string[_meshTracks.Count];
            for (int index = 0; index < _meshTracks.Count; index++)
                options[index] = GetMeshDisplayName(_meshTracks[index].MeshPath);
            return options;
        }

        private int FindMeshIndex(string meshPath)
        {
            for (int index = 0; index < _meshTracks.Count; index++)
            {
                if (string.Equals(_meshTracks[index].MeshPath, meshPath, StringComparison.Ordinal))
                    return index;
            }

            return -1;
        }

        private BlendShapeMeshTrack CurrentMeshTrack
        {
            get
            {
                return _selectedMeshIndex >= 0 && _selectedMeshIndex < _meshTracks.Count
                    ? _meshTracks[_selectedMeshIndex]
                    : null;
            }
        }

        private string GetApplyValidationError()
        {
            if (_sourceClip == null)
                return "Assign an AnimationClip.";

            string settingsError = BlendShapeCurveRemapper.GetSettingsValidationError(_settings);
            if (!string.IsNullOrEmpty(settingsError))
                return settingsError;

            if (_meshTracks.Count == 0)
                return "No blend-shape Mesh track was found.";

            if (_selectedPropertyNames.Count == 0)
                return "Select at least one blend-shape property.";

            if (_analysis == null || _analysis.MatchedCurveCount == 0)
                return "The selected curves were not found.";

            return _overwriteSource
                ? BlendShapeCurveRemapper.GetOverwriteValidationError(_sourceClip)
                : GetCopyValidationError();
        }

        private string GetCopyValidationError()
        {
            if (string.IsNullOrWhiteSpace(_outputAssetPath))
                return "Specify an output .anim path.";

            string path = _outputAssetPath.Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                return "The output asset must be inside the project Assets folder.";

            if (!string.Equals(Path.GetExtension(path), ".anim", StringComparison.OrdinalIgnoreCase))
                return "The output file must use the .anim extension.";

            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) ||
                !AssetDatabase.IsValidFolder(directory.Replace('\\', '/')))
            {
                return "The output folder does not exist.";
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(path) != null)
                return "An asset already exists at the output path.";

            return null;
        }

        private string BuildMappingDescription()
        {
            return string.Format(
                "Input <= {0:0.###} -> {1:0.###} / {0:0.###} < input < {2:0.###} -> {1:0.###}..{3:0.###} / input >= {2:0.###} -> {4:0.###}",
                _settings.LowerThreshold,
                _settings.LowerOutput,
                _settings.UpperThreshold,
                _settings.MiddleMaximumOutput,
                _settings.UpperOutput);
        }

        private void SuggestOutputPath()
        {
            if (_sourceClip == null)
            {
                _outputAssetPath = string.Empty;
                return;
            }

            string sourcePath = AssetDatabase.GetAssetPath(_sourceClip).Replace('\\', '/');
            string directory = "Assets";
            if (sourcePath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                string sourceDirectory = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrEmpty(sourceDirectory))
                    directory = sourceDirectory.Replace('\\', '/');
            }

            string fileName = SanitizeFileName(_sourceClip.name + _copySuffix) + ".anim";
            _outputAssetPath = AssetDatabase.GenerateUniqueAssetPath(directory + "/" + fileName);
        }

        private void BrowseOutputPath()
        {
            string directory = "Assets";
            if (!string.IsNullOrEmpty(_outputAssetPath))
            {
                string currentDirectory = Path.GetDirectoryName(_outputAssetPath);
                if (!string.IsNullOrEmpty(currentDirectory) &&
                    AssetDatabase.IsValidFolder(currentDirectory.Replace('\\', '/')))
                {
                    directory = currentDirectory.Replace('\\', '/');
                }
            }

            string defaultName = _sourceClip != null
                ? SanitizeFileName(_sourceClip.name + _copySuffix)
                : "Animation_BlendShapeRemapped";
            string selectedPath = EditorUtility.SaveFilePanelInProject(
                "Save Processed AnimationClip",
                defaultName,
                "anim",
                "Choose where to save the processed copy.",
                directory);

            if (!string.IsNullOrEmpty(selectedPath))
                _outputAssetPath = selectedPath.Replace('\\', '/');
        }

        private void ResetDefaults()
        {
            _settings = BlendShapeRemapSettings.Default;
            _copySuffix = DefaultCopySuffix;
            _overwriteSource = false;
            SavePreferences();
            SuggestOutputPath();
            RefreshAnalysis();
        }

        private void LoadPreferences()
        {
            BlendShapeRemapSettings defaults = BlendShapeRemapSettings.Default;
            _settings = new BlendShapeRemapSettings
            {
                LowerThreshold = EditorPrefs.GetFloat(
                    PreferencePrefix + "LowerThreshold",
                    defaults.LowerThreshold),
                UpperThreshold = EditorPrefs.GetFloat(
                    PreferencePrefix + "UpperThreshold",
                    defaults.UpperThreshold),
                LowerOutput = EditorPrefs.GetFloat(
                    PreferencePrefix + "LowerOutput",
                    defaults.LowerOutput),
                MiddleMaximumOutput = EditorPrefs.GetFloat(
                    PreferencePrefix + "MiddleMaximumOutput",
                    defaults.MiddleMaximumOutput),
                UpperOutput = EditorPrefs.GetFloat(
                    PreferencePrefix + "UpperOutput",
                    defaults.UpperOutput)
            };

            _copySuffix = EditorPrefs.GetString(
                PreferencePrefix + "CopySuffix",
                DefaultCopySuffix);
            _overwriteSource = EditorPrefs.GetBool(
                PreferencePrefix + "OverwriteSource",
                false);
            _preferredMeshPath = EditorPrefs.GetString(
                PreferencePrefix + "MeshPath",
                string.Empty);
        }

        private void SavePreferences()
        {
            EditorPrefs.SetFloat(
                PreferencePrefix + "LowerThreshold",
                _settings.LowerThreshold);
            EditorPrefs.SetFloat(
                PreferencePrefix + "UpperThreshold",
                _settings.UpperThreshold);
            EditorPrefs.SetFloat(
                PreferencePrefix + "LowerOutput",
                _settings.LowerOutput);
            EditorPrefs.SetFloat(
                PreferencePrefix + "MiddleMaximumOutput",
                _settings.MiddleMaximumOutput);
            EditorPrefs.SetFloat(
                PreferencePrefix + "UpperOutput",
                _settings.UpperOutput);
            EditorPrefs.SetString(
                PreferencePrefix + "CopySuffix",
                _copySuffix ?? string.Empty);
            EditorPrefs.SetBool(
                PreferencePrefix + "OverwriteSource",
                _overwriteSource);
            EditorPrefs.SetString(
                PreferencePrefix + "MeshPath",
                _preferredMeshPath ?? string.Empty);
        }

        private static string GetMeshDisplayName(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath))
                return "<Root>";

            int separatorIndex = meshPath.LastIndexOf('/');
            string leafName = separatorIndex >= 0
                ? meshPath.Substring(separatorIndex + 1)
                : meshPath;
            return leafName + "  [" + meshPath + "]";
        }

        private static string FormatMeshPath(string meshPath)
        {
            return string.IsNullOrEmpty(meshPath) ? "<Root>" : meshPath;
        }

        private static string GetFriendlyPropertyName(string propertyName)
        {
            return propertyName != null &&
                   propertyName.StartsWith(
                       BlendShapeCurveRemapper.BlendShapePropertyPrefix,
                       StringComparison.Ordinal)
                ? propertyName.Substring(BlendShapeCurveRemapper.BlendShapePropertyPrefix.Length)
                : propertyName;
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Animation_BlendShapeRemapped";

            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                builder.Append(Array.IndexOf(invalidCharacters, character) >= 0 ? '_' : character);
            }

            return builder.ToString();
        }
    }
}
