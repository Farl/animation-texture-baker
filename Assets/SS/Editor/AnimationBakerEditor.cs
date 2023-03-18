using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine.Assertions;

namespace SS
{

    public class AnimationBakerEditor : EditorWindow
    {
        #region Enums / Classes

        public static class RenderTextureToTexture2D
        {

            public static Texture2D Convert(RenderTexture rt)
            {
                TextureFormat format;

                switch (rt.format)
                {
                    case RenderTextureFormat.ARGBFloat:
                        format = TextureFormat.RGBAFloat;
                        break;
                    case RenderTextureFormat.ARGBHalf:
                        format = TextureFormat.RGBAHalf;
                        break;
                    case RenderTextureFormat.ARGBInt:
                        format = TextureFormat.RGBA32;
                        break;
                    case RenderTextureFormat.ARGB32:
                        format = TextureFormat.ARGB32;
                        break;
                    default:
                        format = TextureFormat.ARGB32;
                        Debug.LogWarning("Unsuported RenderTextureFormat.");
                        break;
                }

                return Convert(rt, format);
            }

            static Texture2D Convert(RenderTexture rt, TextureFormat format)
            {
                var tex2d = new Texture2D(rt.width, rt.height, format, false);
                var rect = Rect.MinMaxRect(0f, 0f, tex2d.width, tex2d.height);
                RenderTexture.active = rt;
                tex2d.ReadPixels(rect, 0, 0);
                RenderTexture.active = null;
                return tex2d;
            }
        }
        #endregion

        private bool relative = true;
        private bool createPrefab = false;
        private SkinnedMeshRenderer skinnedMeshRenderer
        {
            get
            {
                return _skinnedMeshRenderer;
            }
            set
            {
                if (value != null && _skinnedMeshRenderer != value)
                {
                    animationClips.Clear();
                    var clips = GetAnimationClips(value, out animGO);
                    if (clips != null && clips.Length > 0)
                        animationClips.AddRange(clips);
                }
                _skinnedMeshRenderer = value;
            }
        }
        private SkinnedMeshRenderer _skinnedMeshRenderer;
        private ComputeShader infoTexGen;
        private Shader playShader;
        private string folderName = "BakedAnimationTex";
        private GameObject animGO;
        private GenerateType generateType;
        private List<AnimationClip> animationClips = new List<AnimationClip>();

        public enum GenerateType
        {
            None = 0,
            MeshRenderer,
            ParticleSystem,
        }

        private struct VertInfo
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector3 tangent;
        }

        private void OnEnable()
        {
            if (infoTexGen == null)
            {
                var computeShaders = AssetDatabase.FindAssets("MeshInfoTextureGen");
                if (computeShaders.Length > 0)
                    infoTexGen = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetDatabase.GUIDToAssetPath(computeShaders[0]));
            }

            if (playShader == null)
            {
                var vertexAnimTexShader = Shader.Find("VertexAnimationTexture");
                if (vertexAnimTexShader)
                    playShader = vertexAnimTexShader;
            }
        }

        [MenuItem("Tools/SS/Animation Baker")]
        private static void Open()
        {
            var w = EditorWindow.GetWindow<AnimationBakerEditor>();
            w.titleContent = new GUIContent("Anim Baker");
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            skinnedMeshRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Skinned Mesh Renderer", skinnedMeshRenderer, typeof(SkinnedMeshRenderer), true);
            if (EditorGUI.EndChangeCheck() && skinnedMeshRenderer)
            {
            }
            GUI.enabled = false;
            EditorGUILayout.ObjectField("Anim GameObject", animGO, typeof(GameObject), true);
            GUI.enabled = true;
            infoTexGen = (ComputeShader)EditorGUILayout.ObjectField("Compute Shader", infoTexGen, typeof(ComputeShader), false);
            playShader = (Shader)EditorGUILayout.ObjectField("Visual Shader", playShader, typeof(Shader), false);
            relative = EditorGUILayout.Toggle("Relative", relative);
            createPrefab = EditorGUILayout.Toggle("Create Prefab", createPrefab);
            generateType = (GenerateType)EditorGUILayout.EnumPopup("Generate", generateType);
            folderName = EditorGUILayout.TextField("Folder Name", folderName);
            if (animGO)
                EditorGUILayout.HelpBox($"Output: {folderName}/{animGO.name}/", MessageType.Info);

            if (animationClips.Count > 0)
            {
                EditorGUILayout.BeginVertical(new GUIStyle("Box"));
                foreach (var clip in animationClips)
                {
                    if (GUILayout.Button($"Bake {clip.name}"))
                    {
                        AnimationBake(skinnedMeshRenderer, infoTexGen, playShader, relative, generateType, clip, folderName, createPrefab);
                    }
                }
                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("Bake All"))
            {
                AnimationBake(skinnedMeshRenderer, infoTexGen, playShader, relative, generateType, null, folderName, createPrefab);
            }
        }

        private void OnSelectionChange()
        {
            var go = Selection.activeGameObject;
            if (go)
            {
                var skin = go.GetComponentInChildren<SkinnedMeshRenderer>();
                if (skin)
                {
                    skinnedMeshRenderer = skin;
                }
            }
        }

        private static AnimationClip[] GetAnimationClips(Component component, out GameObject go)
        {
            AnimationClip[] clips = null;
            var animator = component.GetComponentInParent<Animator>();
            go = component.gameObject;

            if (animator && animator.runtimeAnimatorController)
            {
                clips = animator.runtimeAnimatorController.animationClips;
                go = animator.gameObject;
            }
            else
            {
                var animation = component.GetComponentInParent<Animation>();
                if (animation)
                {
                    clips = new AnimationClip[animation.GetClipCount()];
                    int i = 0;
                    foreach (AnimationState state in animation)
                    {
                        clips[i] = state.clip;
                        i++;
                    }
                    go = animation.gameObject;
                }
            }
            return clips;
        }

        public static Vector3 ExtractTranslationFromMatrix(ref Matrix4x4 matrix)
        {
            Vector3 translate;
            translate.x = matrix.m03;
            translate.y = matrix.m13;
            translate.z = matrix.m23;
            return translate;
        }
        public static Quaternion ExtractRotationFromMatrix(ref Matrix4x4 matrix)
        {
            Vector3 forward;
            forward.x = matrix.m02;
            forward.y = matrix.m12;
            forward.z = matrix.m22;

            Vector3 upwards;
            upwards.x = matrix.m01;
            upwards.y = matrix.m11;
            upwards.z = matrix.m21;

            return Quaternion.LookRotation(forward, upwards);
        }
        public static Vector3 ExtractScaleFromMatrix(ref Matrix4x4 matrix)
        {
            Vector3 scale;
            scale.x = new Vector4(matrix.m00, matrix.m10, matrix.m20, matrix.m30).magnitude;
            scale.y = new Vector4(matrix.m01, matrix.m11, matrix.m21, matrix.m31).magnitude;
            scale.z = new Vector4(matrix.m02, matrix.m12, matrix.m22, matrix.m32).magnitude;
            return scale;
        }

        private static void ResetBindPose(SkinnedMeshRenderer skinnedMeshRenderer)
        {
            for (var i = 0; i < skinnedMeshRenderer.bones.Length; i++)
            {
                var matrix = skinnedMeshRenderer.sharedMesh.bindposes[i].inverse;

                var bt = skinnedMeshRenderer.bones[i];
                bt.position = ExtractTranslationFromMatrix(ref matrix);
                bt.rotation = ExtractRotationFromMatrix(ref matrix);
                bt.localScale = ExtractScaleFromMatrix(ref matrix);
            }

        }

        private static void AnimationBake(SkinnedMeshRenderer skinnedMeshRenderer, ComputeShader infoTexGen, Shader playShader, bool relative, GenerateType generateType, AnimationClip clip = null, string folderName = null, bool createPrefab = false)
        {
            Assert.IsNotNull(infoTexGen, "Missing Compute Shader");
            Assert.IsNotNull(playShader, "Missing Shader");
            Assert.IsNotNull(skinnedMeshRenderer, "Missing Skinned Mesh Renderer");
            var skin = skinnedMeshRenderer;
            if (!skin || !infoTexGen || !playShader)
                return;

            // Get clips
            GameObject animGO = null;
            AnimationClip[] clips = GetAnimationClips(skin, out animGO);

            if (animGO == null || clips == null || clips.Length == 0)
                return;

            EditorUtility.DisplayProgressBar("Baking", "", 0);

            ResetBindPose(skin);

            var name = animGO.name;
            var origMesh = skin.sharedMesh;
            var vCount = origMesh.vertexCount;
            var texWidth = Mathf.NextPowerOfTwo(vCount);
            var mesh = new Mesh();

            bool isCanceled = false;

            //animator.speed = 0;
            for (var j = 0; j < clips.Length; j++)
            {
                var c = clips[j];
                if (clip != null && c != clip)
                    continue;

                if (!isCanceled && EditorUtility.DisplayCancelableProgressBar("Baking", c.name, (float)(j) / clips.Length))
                {
                    isCanceled = true;
                    break;
                }

                int fps = 20;
                var frames = Mathf.NextPowerOfTwo((int)(c.length * fps));
                var infoList = new List<VertInfo>();

                var pRt = new RenderTexture(texWidth, frames, 0, RenderTextureFormat.ARGBHalf);
                pRt.name = string.Format("{0}.{1}.posTex", name, c.name);
                var nRt = new RenderTexture(texWidth, frames, 0, RenderTextureFormat.ARGBHalf);
                nRt.name = string.Format("{0}.{1}.normTex", name, c.name);
                var tRt = new RenderTexture(texWidth, frames, 0, RenderTextureFormat.ARGBHalf);
                tRt.name = string.Format("{0}.{1}.tangentTex", name, c.name);

                foreach (var rt in new[] { pRt, nRt, tRt })
                {
                    rt.enableRandomWrite = true;
                    rt.Create();
                    RenderTexture.active = rt;
                    GL.Clear(true, true, Color.clear);
                }

                var origVertices = origMesh.vertices;
                var origNormals = origMesh.normals;
                var origTangents = origMesh.tangents;

                AnimationMode.StartAnimationMode();

                //animator.Play(c.name);

                for (var i = 0; i < frames; i++)
                {
                    var percent = Mathf.Lerp((float)j / clips.Length, (float)(j + 1) / clips.Length, (float)(i + 1) / frames);
                    if (!isCanceled && EditorUtility.DisplayCancelableProgressBar("Baking", c.name, percent))
                    {
                        isCanceled = true;
                        break;
                    }

                    AnimationMode.SampleAnimationClip(animGO, c, (float)i / fps);
                    //animator.Play(c.name, 0, (float)i / frames);

                    skin.BakeMesh(mesh);

                    if (relative)
                    {
                        infoList.AddRange(Enumerable.Range(0, vCount)
                            .Select(idx => new VertInfo()
                            {
                                position = mesh.vertices[idx] - origVertices[idx],
                                normal = mesh.normals[idx] - origNormals[idx],
                                tangent = mesh.tangents[idx] - origTangents[idx]
                            })
                        );
                    }
                    else
                    {
                        infoList.AddRange(Enumerable.Range(0, vCount)
                            .Select(idx => new VertInfo()
                            {
                                position = mesh.vertices[idx],
                                normal = mesh.normals[idx],
                                tangent = mesh.tangents[idx]
                            })
                        );
                    }
                }

                if (AnimationMode.InAnimationMode())
                    AnimationMode.StopAnimationMode();

                if (isCanceled)
                {
                    break;
                }

                var buffer = new ComputeBuffer(infoList.Count, System.Runtime.InteropServices.Marshal.SizeOf(typeof(VertInfo)));
                buffer.SetData(infoList.ToArray());

                var kernel = infoTexGen.FindKernel("CSMain");
                uint x, y, z;
                infoTexGen.GetKernelThreadGroupSizes(kernel, out x, out y, out z);

                infoTexGen.SetInt("VertCount", vCount);
                infoTexGen.SetBuffer(kernel, "Info", buffer);
                infoTexGen.SetTexture(kernel, "OutPosition", pRt);
                infoTexGen.SetTexture(kernel, "OutNormal", nRt);
                infoTexGen.SetTexture(kernel, "OutTangent", tRt);
                infoTexGen.Dispatch(kernel, vCount / (int)x + 1, frames / (int)y + 1, 1);

                buffer.Release();

                if (folderName == null)
                    folderName = "BakedAnimationTex";
                var folderPath = Path.Combine("Assets", folderName);
                if (!AssetDatabase.IsValidFolder(folderPath))
                    AssetDatabase.CreateFolder("Assets", folderName);

                var subFolder = name;
                var subFolderPath = Path.Combine(folderPath, subFolder);
                if (!AssetDatabase.IsValidFolder(subFolderPath))
                    AssetDatabase.CreateFolder(folderPath, subFolder);

                var posTex = RenderTextureToTexture2D.Convert(pRt);
                var normTex = RenderTextureToTexture2D.Convert(nRt);
                var tanTex = RenderTextureToTexture2D.Convert(tRt);
                Graphics.CopyTexture(pRt, posTex);
                Graphics.CopyTexture(nRt, normTex);
                Graphics.CopyTexture(tRt, tanTex);

                var mat = new Material(playShader);
                mat.SetTexture("_MainTex", skin.sharedMaterial.mainTexture);
                mat.SetTexture("_PosTex", posTex);
                mat.SetTexture("_NmlTex", normTex);
                mat.SetFloat("_Length", c.length);
                if (c.isLooping)
                {
                    mat.SetFloat("_Loop", 1f);
                    mat.EnableKeyword("ANIM_LOOP");
                }
                if (generateType.Equals(GenerateType.ParticleSystem))
                {
                    mat.SetInt("_Particle", 1);
                }

                GameObject go = (generateType == GenerateType.None) ? null : new GameObject(name + "." + c.name);
                switch (generateType)
                {
                    case GenerateType.MeshRenderer:
                        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                        go.AddComponent<MeshFilter>().sharedMesh = skin.sharedMesh;
                        break;
                    case GenerateType.ParticleSystem:
                        {
                            var ps = go.AddComponent<ParticleSystem>();
                            var main = ps.main;
                            main.maxParticles = 2;
                            main.startRotation3D = true;
                            main.startRotationXMultiplier = 1f;
                            main.startRotationYMultiplier = 1f;
                            main.startRotationZMultiplier = 1f;
                            main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2);
                            main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2);
                            main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2);
                            var psr = go.GetComponent<ParticleSystemRenderer>();
                            psr.renderMode = ParticleSystemRenderMode.Mesh;
                            psr.mesh = skin.sharedMesh;
                            psr.alignment = ParticleSystemRenderSpace.World;
                            psr.material = mat;
                            var vs = new List<ParticleSystemVertexStream>();
                            vs.Add(ParticleSystemVertexStream.Position);
                            vs.Add(ParticleSystemVertexStream.Normal);
                            vs.Add(ParticleSystemVertexStream.Color);
                            vs.Add(ParticleSystemVertexStream.UV);
                            vs.Add(ParticleSystemVertexStream.Rotation3D);
                            vs.Add(ParticleSystemVertexStream.VertexID);
                            psr.SetActiveVertexStreams(vs);
                        }
                        break;
                }

                AssetDatabase.CreateAsset(posTex, Path.Combine(subFolderPath, pRt.name + ".asset"));
                AssetDatabase.CreateAsset(normTex, Path.Combine(subFolderPath, nRt.name + ".asset"));
                AssetDatabase.CreateAsset(tanTex, Path.Combine(subFolderPath, tRt.name + ".asset"));
                AssetDatabase.CreateAsset(mat, Path.Combine(subFolderPath, string.Format("{0}.{1}.animTex.asset", name, c.name)));

                if (go && createPrefab)
                {
                    //PrefabUtility.CreatePrefab(Path.Combine(folderPath, go.name + ".prefab").Replace("\\", "/"), go);
                    PrefabUtility.SaveAsPrefabAssetAndConnect(go, Path.Combine(folderPath, go.name + ".prefab").Replace("\\", "/"), InteractionMode.AutomatedAction);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }


            ResetBindPose(skin);

            EditorUtility.ClearProgressBar();
        }
    }
}