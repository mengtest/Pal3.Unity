﻿// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2023, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.Rendering.Renderer
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using Core.DataLoader;
    using Core.DataReader.Pol;
    using Core.Extensions;
    using Core.GameBox;
    using Core.Renderer;
    using Dev;
    using Material;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    /// <summary>
    /// Poly(.pol) model renderer
    /// </summary>
    public class PolyModelRenderer : MonoBehaviour, IDisposable
    {
        private const string ANIMATED_WATER_TEXTURE_DEFAULT_NAME_PREFIX = "w00";
        private const string ANIMATED_WATER_TEXTURE_DEFAULT_NAME = "w0001";
        private const string ANIMATED_WATER_TEXTURE_DEFAULT_EXTENSION = ".dds";
        private const int ANIMATED_WATER_ANIMATION_FRAMES = 30;
        private const float ANIMATED_WATER_ANIMATION_FPS = 20f;

        private ITextureResourceProvider _textureProvider;
        private IMaterialFactory _materialFactory;
        private readonly List<Coroutine> _waterAnimations = new ();
        private Dictionary<string, Texture2D> _textureCache = new ();

        private bool _isStaticObject;
        private Color _tintColor;
        private bool _isWaterSurfaceOpaque;

        private readonly int _mainTexturePropertyId = Shader.PropertyToID("_MainTex");

        public void Render(PolFile polFile,
            ITextureResourceProvider textureProvider,
            IMaterialFactory materialFactory,
            bool isStaticObject,
            Color? tintColor = default,
            bool isWaterSurfaceOpaque = default)
        {
            _textureProvider = textureProvider;
            _materialFactory = materialFactory;
            _isStaticObject = isStaticObject;
            _tintColor = tintColor ?? Color.white;
            _isWaterSurfaceOpaque = isWaterSurfaceOpaque;
            _textureCache = BuildTextureCache(polFile, textureProvider);

            for (var i = 0; i < polFile.Meshes.Length; i++)
            {
                RenderMeshInternal(
                    polFile.NodeDescriptions[i],
                    polFile.Meshes[i]);
            }
        }

        public Bounds GetRendererBounds()
        {
            var renderers = GetComponentsInChildren<StaticMeshRenderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(transform.position, Vector3.one);
            }
            Bounds bounds = renderers[0].GetRendererBounds();
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].GetRendererBounds());
            }
            return bounds;
        }

        public Bounds GetMeshBounds()
        {
            var renderers = GetComponentsInChildren<StaticMeshRenderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.one);
            }
            Bounds bounds = renderers[0].GetMeshBounds();
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].GetMeshBounds());
            }
            return bounds;
        }

        private Dictionary<string, Texture2D> BuildTextureCache(PolFile polFile,
            ITextureResourceProvider textureProvider)
        {
            Dictionary<string, Texture2D> textureCache = new();
            foreach (PolMesh mesh in polFile.Meshes)
            {
                foreach (PolTexture texture in mesh.Textures)
                {
                    foreach (var textureName in texture.Material.TextureFileNames)
                    {
                        if (string.IsNullOrEmpty(textureName)) continue;
                        if (textureCache.ContainsKey(textureName)) continue;

                        Texture2D texture2D;

                        if (_materialFactory.ShaderType == MaterialShaderType.Lit)
                        {
                            // No need to load pre-baked shadow texture if
                            // material is lit material, since shadow texture
                            // will be generated by shader in runtime.
                            // Note: all shadow texture name starts with "^"
                            texture2D = textureName.StartsWith("^") ?
                                null : textureProvider.GetTexture(textureName);
                        }
                        else
                        {
                            texture2D = textureProvider.GetTexture(textureName);
                        }

                        textureCache[textureName] = texture2D;
                    }
                }
            }
            return textureCache;
        }

        private void RenderMeshInternal(PolGeometryNode meshNode, PolMesh mesh)
        {
            for (var i = 0; i < mesh.Textures.Length; i++)
            {
                var textures = new List<(string name, Texture2D texture)>();
                foreach (var textureName in mesh.Textures[i].Material.TextureFileNames)
                {
                    if (string.IsNullOrEmpty(textureName))
                    {
                        textures.Add((textureName, Texture2D.whiteTexture));
                        continue;
                    }

                    if (_textureCache.TryGetValue(textureName, out Texture2D textureInCache))
                    {
                        textures.Add((textureName, textureInCache));
                    }
                }

                if (textures.Count == 0)
                {
                    Debug.LogWarning($"0 texture found for {meshNode.Name}");
                    return;
                }

                GameObject meshObject = new (meshNode.Name)
                {
                    isStatic = _isStaticObject
                };

                // Attach BlendFlag and GameBoxMaterial to the GameObject for better debuggability
                #if UNITY_EDITOR
                var materialInfoPresenter = meshObject.AddComponent<MaterialInfoPresenter>();
                materialInfoPresenter.blendFlag = mesh.Textures[i].BlendFlag;
                materialInfoPresenter.material = mesh.Textures[i].Material;
                #endif

                var meshRenderer = meshObject.AddComponent<StaticMeshRenderer>();
                var blendFlag = mesh.Textures[i].BlendFlag;

                Material[] CreateMaterials(bool isWaterSurface, int mainTextureIndex, int shadowTextureIndex = -1)
                {
                    Material[] materials;
                    float waterSurfaceOpacity = 1.0f;

                    if (isWaterSurface)
                    {
                        materials = new Material[1];

                        if (!_isWaterSurfaceOpaque)
                        {
                            waterSurfaceOpacity = textures[mainTextureIndex].texture.GetPixel(0, 0).a;
                        }
                        else
                        {
                            blendFlag = GameBoxBlendFlag.Opaque;
                        }

                        materials[0] = _materialFactory.CreateWaterMaterial(
                            textures[mainTextureIndex],
                            shadowTextureIndex >= 0 ? textures[shadowTextureIndex] : (null, null),
                            waterSurfaceOpacity,
                            blendFlag);
                    }
                    else
                    {
                        materials = _materialFactory.CreateStandardMaterials(
                            RendererType.Pol,
                            textures[mainTextureIndex],
                            shadowTextureIndex >= 0 ? textures[shadowTextureIndex] : (null, null),
                            _tintColor,
                            blendFlag);
                    }
                    return materials;
                }

                if (textures.Count >= 1)
                {
                    int mainTextureIndex = textures.Count == 1 ? 0 : 1;
                    int shadowTextureIndex = textures.Count == 1 ? -1 : 0;

                    bool isWaterSurface = textures[mainTextureIndex].name
                        .StartsWith(ANIMATED_WATER_TEXTURE_DEFAULT_NAME, StringComparison.OrdinalIgnoreCase);

                    Material[] materials = CreateMaterials(isWaterSurface, mainTextureIndex, shadowTextureIndex);

                    if (isWaterSurface)
                    {
                        StartWaterSurfaceAnimation(materials[0], textures[mainTextureIndex].texture);
                    }

                    _ = meshRenderer.Render(ref mesh.VertexInfo.Positions,
                        ref mesh.Textures[i].Triangles,
                        ref mesh.VertexInfo.Normals,
                        ref mesh.VertexInfo.Uvs[mainTextureIndex],
                        ref mesh.VertexInfo.Uvs[Math.Max(shadowTextureIndex, 0)],
                        ref materials,
                        false);
                }

                meshObject.transform.SetParent(transform, false);
            }
        }

        private IEnumerator AnimateWaterTextureAsync(Material material, Texture2D defaultTexture)
        {
            var waterTextures = new List<Texture2D> { defaultTexture };

            for (var i = 2; i <= ANIMATED_WATER_ANIMATION_FRAMES; i++)
            {
                Texture2D texture = _textureProvider.GetTexture(
                    ANIMATED_WATER_TEXTURE_DEFAULT_NAME_PREFIX +
                    $"{i:00}" +
                    ANIMATED_WATER_TEXTURE_DEFAULT_EXTENSION);
                waterTextures.Add(texture);
            }

            var waterAnimationDelay = new WaitForSeconds(1 / ANIMATED_WATER_ANIMATION_FPS);

            while (isActiveAndEnabled)
            {
                for (var i = 0; i < ANIMATED_WATER_ANIMATION_FRAMES; i++)
                {
                    material.SetTexture(_mainTexturePropertyId, waterTextures[i]);
                    yield return waterAnimationDelay;
                }
            }
        }

        private void StartWaterSurfaceAnimation(Material material, Texture2D defaultTexture)
        {
            _waterAnimations.Add(StartCoroutine(AnimateWaterTextureAsync(material, defaultTexture)));
        }

        private void OnDisable()
        {
            Dispose();
        }

        public void Dispose()
        {
            foreach (Coroutine waterAnimation in _waterAnimations)
            {
                if (waterAnimation != null)
                {
                    StopCoroutine(waterAnimation);
                }
            }

            foreach (StaticMeshRenderer meshRenderer in GetComponentsInChildren<StaticMeshRenderer>())
            {
                _materialFactory.ReturnToPool(meshRenderer.GetMaterials());
                meshRenderer.gameObject.Destroy();
            }
        }
    }
}