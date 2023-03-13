using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine.Assertions;

public class AnimationBakerEditor : EditorWindow
{
    private bool relative = false;
    private SkinnedMeshRenderer skinnedMeshRenderer;
    private ComputeShader infoTexGen;
    private Shader playShader;
    private string folderName = "BakedAnimationTex";
    private Animator animator;

    private struct VertInfo
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector3 tangent;
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
            animator = skinnedMeshRenderer.GetComponentInParent<Animator>();
        }
        GUI.enabled = false;
        EditorGUILayout.ObjectField("Animator", animator, typeof(Animator), true);
        GUI.enabled = true;
        infoTexGen = (ComputeShader)EditorGUILayout.ObjectField("Compute Shader", infoTexGen, typeof(ComputeShader), false);
        playShader = (Shader)EditorGUILayout.ObjectField("Visual Shader", playShader, typeof(Shader), false);
        relative = EditorGUILayout.Toggle("Relative", relative);
        folderName = EditorGUILayout.TextField("Folder Name", folderName);
        if (animator)
            EditorGUILayout.LabelField($"Output: {folderName}/{animator.name}/");

        if (GUILayout.Button("Bake"))
        {
            AnimationBake(skinnedMeshRenderer, infoTexGen, playShader, relative, folderName);
        }
    }

    private static void AnimationBake(SkinnedMeshRenderer skinnedMeshRenderer, ComputeShader infoTexGen, Shader playShader, bool relative, string folderName = null)
    {
        Assert.IsNotNull(infoTexGen, "Missing Compute Shader");
        Assert.IsNotNull(playShader, "Missing Shader");
        Assert.IsNotNull(skinnedMeshRenderer, "Missing Skinned Mesh Renderer");
        var skin = skinnedMeshRenderer;
        if (!skin || !infoTexGen || !playShader)
            return;

        var animator = skin.GetComponentInParent<Animator>();

        Assert.IsNotNull(animator, "Missing Animator");
        if (!animator || !animator.runtimeAnimatorController)
            return;

        EditorUtility.DisplayProgressBar("Baking", "", 0);

        var name = animator.name;
        var clips = animator.runtimeAnimatorController.animationClips;

        var origMesh = skin.sharedMesh;
        var vCount = origMesh.vertexCount;
        var texWidth = Mathf.NextPowerOfTwo(vCount);
        var mesh = new Mesh();

        bool isCanceled = false;

        animator.speed = 0;
        for (var j = 0; j < clips.Length; j++)
        {
            var c = clips[j];
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

                AnimationMode.SampleAnimationClip(animator.gameObject, c, (float)i / fps);
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
                EditorUtility.ClearProgressBar();
                return;
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

            var go = new GameObject(name + "." + c.name);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.AddComponent<MeshFilter>().sharedMesh = skin.sharedMesh;

            AssetDatabase.CreateAsset(posTex, Path.Combine(subFolderPath, pRt.name + ".asset"));
            AssetDatabase.CreateAsset(normTex, Path.Combine(subFolderPath, nRt.name + ".asset"));
            AssetDatabase.CreateAsset(tanTex, Path.Combine(subFolderPath, tRt.name + ".asset"));
            AssetDatabase.CreateAsset(mat, Path.Combine(subFolderPath, string.Format("{0}.{1}.animTex.asset", name, c.name)));

            //PrefabUtility.CreatePrefab(Path.Combine(folderPath, go.name + ".prefab").Replace("\\", "/"), go);
            PrefabUtility.SaveAsPrefabAsset(go, Path.Combine(folderPath, go.name + ".prefab").Replace("\\", "/"));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.ClearProgressBar();
        }
    }
}