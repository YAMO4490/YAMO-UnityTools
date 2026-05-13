using UnityEditor;
using UnityEngine;
using YAMO.UnityTools;

namespace YAMO.UnityTools.Editor
{
    [CustomEditor(typeof(YamoCam))]
    [CanEditMultipleObjects]
    public class YamoCamEditor : UnityEditor.Editor
    {
        private SerializedProperty enableFollow, followTargets, positionOffset;
        private SerializedProperty followSmoothSpeed, followDistanceElasticity, followFrameInterval;
        private SerializedProperty followX, followY, followZ;
        private SerializedProperty moveRatioX, moveRatioY, moveRatioZ;

        private SerializedProperty enableLookAt, lookAtTargets, lookAtOffset;
        private SerializedProperty lookAtSmoothSpeed, worldUp, lookAtFrameInterval;
        private SerializedProperty rotateX, rotateY, rotateZ;
        private SerializedProperty rotateRatioX, rotateRatioY, rotateRatioZ;

        private SerializedProperty enableOrbital, orbitCenters;
        private SerializedProperty orbitHorizontalRadius, orbitHorizontalSpeed, orbitHorizontalPhaseOffset;
        private SerializedProperty orbitVerticalRadius, orbitVerticalSpeed, orbitVerticalPhaseOffset;
        private SerializedProperty orbitVerticalAngleMin, orbitVerticalAngleMax;
        private SerializedProperty orbitHeightOffset;

        private SerializedProperty enableNoise;
        private SerializedProperty posNoiseAmplitude, posNoiseFrequency;
        private SerializedProperty posNoiseX, posNoiseY, posNoiseZ;
        private SerializedProperty rotNoiseAmplitude, rotNoiseFrequency;
        private SerializedProperty rotNoiseX, rotNoiseY, rotNoiseZ;

        private SerializedProperty updateInEditMode;
        private SerializedProperty applyPlayModeChangesToEditor;

        private void OnEnable()
        {
            enableFollow = serializedObject.FindProperty("enableFollow");
            followTargets = serializedObject.FindProperty("followTargets");
            positionOffset = serializedObject.FindProperty("positionOffset");
            followSmoothSpeed = serializedObject.FindProperty("followSmoothSpeed");
            followDistanceElasticity = serializedObject.FindProperty("followDistanceElasticity");
            followFrameInterval = serializedObject.FindProperty("followFrameInterval");
            followX = serializedObject.FindProperty("followX");
            followY = serializedObject.FindProperty("followY");
            followZ = serializedObject.FindProperty("followZ");
            moveRatioX = serializedObject.FindProperty("moveRatioX");
            moveRatioY = serializedObject.FindProperty("moveRatioY");
            moveRatioZ = serializedObject.FindProperty("moveRatioZ");

            enableLookAt = serializedObject.FindProperty("enableLookAt");
            lookAtTargets = serializedObject.FindProperty("lookAtTargets");
            lookAtOffset = serializedObject.FindProperty("lookAtOffset");
            lookAtSmoothSpeed = serializedObject.FindProperty("lookAtSmoothSpeed");
            worldUp = serializedObject.FindProperty("worldUp");
            lookAtFrameInterval = serializedObject.FindProperty("lookAtFrameInterval");
            rotateX = serializedObject.FindProperty("rotateX");
            rotateY = serializedObject.FindProperty("rotateY");
            rotateZ = serializedObject.FindProperty("rotateZ");
            rotateRatioX = serializedObject.FindProperty("rotateRatioX");
            rotateRatioY = serializedObject.FindProperty("rotateRatioY");
            rotateRatioZ = serializedObject.FindProperty("rotateRatioZ");

            enableOrbital = serializedObject.FindProperty("enableOrbital");
            orbitCenters = serializedObject.FindProperty("orbitCenters");
            orbitHorizontalRadius = serializedObject.FindProperty("orbitHorizontalRadius");
            orbitHorizontalSpeed = serializedObject.FindProperty("orbitHorizontalSpeed");
            orbitHorizontalPhaseOffset = serializedObject.FindProperty("orbitHorizontalPhaseOffset");
            orbitVerticalRadius = serializedObject.FindProperty("orbitVerticalRadius");
            orbitVerticalSpeed = serializedObject.FindProperty("orbitVerticalSpeed");
            orbitVerticalPhaseOffset = serializedObject.FindProperty("orbitVerticalPhaseOffset");
            orbitVerticalAngleMin = serializedObject.FindProperty("orbitVerticalAngleMin");
            orbitVerticalAngleMax = serializedObject.FindProperty("orbitVerticalAngleMax");
            orbitHeightOffset = serializedObject.FindProperty("orbitHeightOffset");

            enableNoise = serializedObject.FindProperty("enableNoise");
            posNoiseAmplitude = serializedObject.FindProperty("posNoiseAmplitude");
            posNoiseFrequency = serializedObject.FindProperty("posNoiseFrequency");
            posNoiseX = serializedObject.FindProperty("posNoiseX");
            posNoiseY = serializedObject.FindProperty("posNoiseY");
            posNoiseZ = serializedObject.FindProperty("posNoiseZ");
            rotNoiseAmplitude = serializedObject.FindProperty("rotNoiseAmplitude");
            rotNoiseFrequency = serializedObject.FindProperty("rotNoiseFrequency");
            rotNoiseX = serializedObject.FindProperty("rotNoiseX");
            rotNoiseY = serializedObject.FindProperty("rotNoiseY");
            rotNoiseZ = serializedObject.FindProperty("rotNoiseZ");

            updateInEditMode = serializedObject.FindProperty("updateInEditMode");
            applyPlayModeChangesToEditor = serializedObject.FindProperty("applyPlayModeChangesToEditor");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // ── Follow Section ──
            DrawSectionHeader("Follow", enableFollow);
            if (enableFollow.boolValue || enableFollow.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(followTargets, new GUIContent("Targets"), true);
                EditorGUILayout.PropertyField(positionOffset, new GUIContent("Position Offset (Local)"));
                EditorGUILayout.Space(4);

                EditorGUILayout.PropertyField(followSmoothSpeed, new GUIContent("Smooth Speed"));
                EditorGUILayout.PropertyField(followDistanceElasticity, new GUIContent("Distance Elasticity"));
                EditorGUILayout.PropertyField(followFrameInterval, new GUIContent("Frame Interval"));
                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField("Local Axis On/Off", EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                ToggleLeftMulti("X", followX);
                ToggleLeftMulti("Y", followY);
                ToggleLeftMulti("Z", followZ);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(2);

                EditorGUILayout.LabelField("Local Axis Move Ratio", EditorStyles.miniLabel);
                if (followX.boolValue) EditorGUILayout.Slider(moveRatioX, 0f, 100f, "X Ratio %");
                if (followY.boolValue) EditorGUILayout.Slider(moveRatioY, 0f, 100f, "Y Ratio %");
                if (followZ.boolValue) EditorGUILayout.Slider(moveRatioZ, 0f, 100f, "Z Ratio %");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);

            // ── LookAt Section ──
            DrawSectionHeader("LookAt", enableLookAt);
            if (enableLookAt.boolValue || enableLookAt.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(lookAtTargets, new GUIContent("Targets"), true);
                EditorGUILayout.PropertyField(lookAtOffset, new GUIContent("Offset"));
                EditorGUILayout.Space(4);

                EditorGUILayout.PropertyField(lookAtSmoothSpeed, new GUIContent("Smooth Speed"));
                EditorGUILayout.PropertyField(worldUp, new GUIContent("World Up"));
                EditorGUILayout.PropertyField(lookAtFrameInterval, new GUIContent("Frame Interval"));
                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField("Axis On/Off", EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                ToggleLeftMulti("X", rotateX);
                ToggleLeftMulti("Y", rotateY);
                ToggleLeftMulti("Z", rotateZ);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(2);

                EditorGUILayout.LabelField("Axis Rotate Ratio", EditorStyles.miniLabel);
                if (rotateX.boolValue) EditorGUILayout.Slider(rotateRatioX, 0f, 100f, "X Ratio %");
                if (rotateY.boolValue) EditorGUILayout.Slider(rotateRatioY, 0f, 100f, "Y Ratio %");
                if (rotateZ.boolValue) EditorGUILayout.Slider(rotateRatioZ, 0f, 100f, "Z Ratio %");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);

            // ── Orbital Section ──
            DrawSectionHeader("Orbital", enableOrbital);
            if (enableOrbital.boolValue || enableOrbital.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(orbitCenters, new GUIContent("Centers (비워두면 Follow Targets)"), true);
                EditorGUILayout.PropertyField(orbitHeightOffset, new GUIContent("Height Offset"));
                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField("Horizontal (360° Loop)", EditorStyles.miniLabel);
                EditorGUILayout.PropertyField(orbitHorizontalRadius, new GUIContent("Radius"));
                EditorGUILayout.PropertyField(orbitHorizontalSpeed, new GUIContent("Speed (°/s)"));
                EditorGUILayout.PropertyField(orbitHorizontalPhaseOffset, new GUIContent("Phase Offset (°)"));
                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField("Vertical (Ping-Pong)", EditorStyles.miniLabel);
                EditorGUILayout.PropertyField(orbitVerticalRadius, new GUIContent("Radius"));
                EditorGUILayout.PropertyField(orbitVerticalSpeed, new GUIContent("Speed (°/s)"));
                EditorGUILayout.PropertyField(orbitVerticalPhaseOffset, new GUIContent("Phase Offset (°)"));
                EditorGUILayout.PropertyField(orbitVerticalAngleMin, new GUIContent("Angle Min (°)"));
                EditorGUILayout.PropertyField(orbitVerticalAngleMax, new GUIContent("Angle Max (°)"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);

            // ── Noise Section ──
            DrawSectionHeader("Noise (Hand-held)", enableNoise);
            if (enableNoise.boolValue || enableNoise.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("Position Noise", EditorStyles.miniLabel);
                EditorGUILayout.PropertyField(posNoiseAmplitude, new GUIContent("Amplitude (m)"));
                EditorGUILayout.PropertyField(posNoiseFrequency, new GUIContent("Frequency"));
                EditorGUILayout.BeginHorizontal();
                ToggleLeftMulti("X", posNoiseX);
                ToggleLeftMulti("Y", posNoiseY);
                ToggleLeftMulti("Z", posNoiseZ);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField("Rotation Noise", EditorStyles.miniLabel);
                EditorGUILayout.PropertyField(rotNoiseAmplitude, new GUIContent("Amplitude (°)"));
                EditorGUILayout.PropertyField(rotNoiseFrequency, new GUIContent("Frequency"));
                EditorGUILayout.BeginHorizontal();
                ToggleLeftMulti("X", rotNoiseX);
                ToggleLeftMulti("Y", rotNoiseY);
                ToggleLeftMulti("Z", rotNoiseZ);
                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);

            // ── Editor Options ──
            EditorGUILayout.LabelField("Editor Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(updateInEditMode);
            EditorGUILayout.PropertyField(applyPlayModeChangesToEditor, new GUIContent(
                "Apply PlayMode Changes to Editor",
                "체크 시 PlayMode 종료 후 변경된 세팅값을 Editor에 저장합니다. (Undo 가능)"));

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSectionHeader(string label, SerializedProperty toggle)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.showMixedValue = toggle.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            bool newVal = EditorGUILayout.Toggle(toggle.boolValue, GUILayout.Width(16));
            if (EditorGUI.EndChangeCheck()) toggle.boolValue = newVal;
            EditorGUI.showMixedValue = false;
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
        }

        private static void ToggleLeftMulti(string label, SerializedProperty prop)
        {
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            bool newVal = EditorGUILayout.ToggleLeft(label, prop.boolValue, GUILayout.Width(40));
            if (EditorGUI.EndChangeCheck()) prop.boolValue = newVal;
            EditorGUI.showMixedValue = false;
        }
    }
}
