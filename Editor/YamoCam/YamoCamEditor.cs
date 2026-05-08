using UnityEditor;
using UnityEngine;
using YAMO.UnityTools;

namespace YAMO.UnityTools.Editor
{
    [CustomEditor(typeof(YamoCam))]
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
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // ── Follow Section ──
            DrawSectionHeader("Follow", enableFollow);
            if (enableFollow.boolValue)
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
                followX.boolValue = EditorGUILayout.ToggleLeft("X", followX.boolValue, GUILayout.Width(40));
                followY.boolValue = EditorGUILayout.ToggleLeft("Y", followY.boolValue, GUILayout.Width(40));
                followZ.boolValue = EditorGUILayout.ToggleLeft("Z", followZ.boolValue, GUILayout.Width(40));
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
            if (enableLookAt.boolValue)
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
                rotateX.boolValue = EditorGUILayout.ToggleLeft("X", rotateX.boolValue, GUILayout.Width(40));
                rotateY.boolValue = EditorGUILayout.ToggleLeft("Y", rotateY.boolValue, GUILayout.Width(40));
                rotateZ.boolValue = EditorGUILayout.ToggleLeft("Z", rotateZ.boolValue, GUILayout.Width(40));
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
            if (enableOrbital.boolValue)
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
            if (enableNoise.boolValue)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("Position Noise", EditorStyles.miniLabel);
                EditorGUILayout.PropertyField(posNoiseAmplitude, new GUIContent("Amplitude (m)"));
                EditorGUILayout.PropertyField(posNoiseFrequency, new GUIContent("Frequency"));
                EditorGUILayout.BeginHorizontal();
                posNoiseX.boolValue = EditorGUILayout.ToggleLeft("X", posNoiseX.boolValue, GUILayout.Width(40));
                posNoiseY.boolValue = EditorGUILayout.ToggleLeft("Y", posNoiseY.boolValue, GUILayout.Width(40));
                posNoiseZ.boolValue = EditorGUILayout.ToggleLeft("Z", posNoiseZ.boolValue, GUILayout.Width(40));
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField("Rotation Noise", EditorStyles.miniLabel);
                EditorGUILayout.PropertyField(rotNoiseAmplitude, new GUIContent("Amplitude (°)"));
                EditorGUILayout.PropertyField(rotNoiseFrequency, new GUIContent("Frequency"));
                EditorGUILayout.BeginHorizontal();
                rotNoiseX.boolValue = EditorGUILayout.ToggleLeft("X", rotNoiseX.boolValue, GUILayout.Width(40));
                rotNoiseY.boolValue = EditorGUILayout.ToggleLeft("Y", rotNoiseY.boolValue, GUILayout.Width(40));
                rotNoiseZ.boolValue = EditorGUILayout.ToggleLeft("Z", rotNoiseZ.boolValue, GUILayout.Width(40));
                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);

            // ── Editor Options ──
            EditorGUILayout.LabelField("Editor Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(updateInEditMode);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSectionHeader(string label, SerializedProperty toggle)
        {
            EditorGUILayout.BeginHorizontal();
            toggle.boolValue = EditorGUILayout.Toggle(toggle.boolValue, GUILayout.Width(16));
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
        }
    }
}
