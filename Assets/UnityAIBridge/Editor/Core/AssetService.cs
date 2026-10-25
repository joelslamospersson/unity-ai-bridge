using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.UI;

namespace UnityAIBridge
{
    public sealed class SpriteImportOptions
    {
        public string SpriteMode = "Single";
        public float PixelsPerUnit = 100f;
        public string Filter = "Bilinear";
        public string Compression = "Compressed";
        public bool AlphaIsTransparency = true;
        public int MaxSize = 2048;
        public bool Mipmaps = false;
        public bool Readable = false;
        public string Pivot = "Center";

        public static SpriteImportOptions FromJson(JsonValue json)
        {
            var options = new SpriteImportOptions();
            if (json == null || !json.IsObject) return options;

            string mode = json["sprite_mode"].AsString(json["spriteMode"].AsString("Single"));
            options.SpriteMode = string.Equals(mode, "multiple", StringComparison.OrdinalIgnoreCase) ? "Multiple" : "Single";
            options.PixelsPerUnit = (float)json["pixels_per_unit"].AsNumber(json["ppu"].AsNumber(100));
            if (options.PixelsPerUnit <= 0) options.PixelsPerUnit = 100f;
            string filter = json["filter"].AsString("Bilinear");
            options.Filter = filter;
            options.Compression = json["compression"].AsString("Compressed");
            options.AlphaIsTransparency = json["alpha_is_transparency"].AsBool(json["alpha"].AsBool(true));
            options.MaxSize = Mathf.Clamp(json["max_size"].AsInt(json["maxSize"].AsInt(2048)), 32, 8192);
            options.Mipmaps = json["mipmaps"].AsBool(false);
            options.Readable = json["readable"].AsBool(false);
            options.Pivot = json["pivot"].AsString("Center");
            return options;
        }

        public JsonValue ToJson()
        {
            return JsonValue.Obj(
                "sprite_mode", SpriteMode,
                "pixels_per_unit", PixelsPerUnit,
                "filter", Filter,
                "compression", Compression,
                "alpha_is_transparency", AlphaIsTransparency,
                "max_size", MaxSize,
                "mipmaps", Mipmaps,
                "readable", Readable,
                "pivot", Pivot);
        }

        public void ApplyTo(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = string.Equals(SpriteMode, "Multiple", StringComparison.OrdinalIgnoreCase) ? SpriteImportMode.Multiple : SpriteImportMode.Single;
            importer.spritePixelsPerUnit = PixelsPerUnit;
            importer.filterMode = ParseFilter(Filter);
            importer.textureCompression = ParseCompression(Compression);
            importer.alphaIsTransparency = AlphaIsTransparency;
            importer.maxTextureSize = MaxSize;
            importer.mipmapEnabled = Mipmaps;
            importer.isReadable = Readable;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.npotScale = TextureImporterNPOTScale.None;

            if (importer.spriteImportMode == SpriteImportMode.Single)
                importer.spritePivot = ParsePivot(Pivot);

            // Platform texture overrides for Android/iOS; ignored by other build targets.
            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = MaxSize;
            android.format = TextureImporterFormat.ETC2_RGBA8;
            android.compressionQuality = 50;
            importer.SetPlatformTextureSettings(android);

            var iphone = importer.GetPlatformTextureSettings("iPhone");
            iphone.overridden = true;
            iphone.maxTextureSize = MaxSize;
            iphone.format = TextureImporterFormat.ASTC_6x6;
            iphone.compressionQuality = 50;
            importer.SetPlatformTextureSettings(iphone);
        }

        static FilterMode ParseFilter(string value)
        {
            if (string.Equals(value, "Point", StringComparison.OrdinalIgnoreCase)) return FilterMode.Point;
            if (string.Equals(value, "Trilinear", StringComparison.OrdinalIgnoreCase)) return FilterMode.Trilinear;
            return FilterMode.Bilinear;
        }

        static TextureImporterCompression ParseCompression(string value)
        {
            if (string.Equals(value, "Uncompressed", StringComparison.OrdinalIgnoreCase)) return TextureImporterCompression.Uncompressed;
            if (string.Equals(value, "CompressedHQ", StringComparison.OrdinalIgnoreCase)) return TextureImporterCompression.CompressedHQ;
            if (string.Equals(value, "CompressedLQ", StringComparison.OrdinalIgnoreCase)) return TextureImporterCompression.CompressedLQ;
            return TextureImporterCompression.Compressed;
        }

        static Vector2 ParsePivot(string value)
        {
            if (string.Equals(value, "Bottom", StringComparison.OrdinalIgnoreCase)) return new Vector2(0.5f, 0f);
            if (string.Equals(value, "Top", StringComparison.OrdinalIgnoreCase)) return new Vector2(0.5f, 1f);
            if (string.Equals(value, "Left", StringComparison.OrdinalIgnoreCase)) return new Vector2(0f, 0.5f);
            if (string.Equals(value, "Right", StringComparison.OrdinalIgnoreCase)) return new Vector2(1f, 0.5f);
            if (string.Equals(value, "BottomLeft", StringComparison.OrdinalIgnoreCase)) return Vector2.zero;
            return new Vector2(0.5f, 0.5f);
        }
    }

    public static class AssetService
    {
        public static string EnsureAssetFolder(string assetFolder)
        {
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(assetFolder) || assetFolder == "Assets") return "Assets";
            if (AssetDatabase.IsValidFolder(assetFolder)) return assetFolder;

            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
            return current;
        }

        public static string ImportImage(string sourceFullPath, string destinationAssetPath, SpriteImportOptions options, out string error)
        {
            error = null;
            string full;
            error = Permissions.CheckWrite(destinationAssetPath, out full, false, true);
            if (error != null) return null;

            if (!File.Exists(sourceFullPath))
            {
                error = "Source image not found: " + sourceFullPath;
                return null;
            }

            try
            {
                string extension = Path.GetExtension(sourceFullPath).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".tga" && extension != ".bmp")
                {
                    error = "Unsupported image format: " + extension;
                    return null;
                }

                string destinationExtension = Path.GetExtension(full).ToLowerInvariant();
                if (!string.Equals(extension, destinationExtension, StringComparison.OrdinalIgnoreCase) &&
                    !(extension == ".jpg" && destinationExtension == ".jpeg"))
                {
                    // Keep the file extension honest; it also keeps Unity's importer selection sane.
                    full = Path.ChangeExtension(full, extension);
                    destinationAssetPath = BridgePaths.ToAssetPath(full);
                }

                if (File.Exists(full)) FileService.BackupFile(full, "before sprite import");
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.Copy(sourceFullPath, full, true);

                AssetDatabase.ImportAsset(destinationAssetPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(destinationAssetPath) as TextureImporter;
                if (importer == null)
                {
                    error = "Unity did not create a TextureImporter for " + destinationAssetPath + ".";
                    return null;
                }

                options = options ?? new SpriteImportOptions();
                options.ApplyTo(importer);
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
                BridgeLog.Success("assets", "Imported sprite " + destinationAssetPath + " (" + options.SpriteMode + ").");
                return destinationAssetPath;
            }
            catch (Exception e)
            {
                error = "Sprite import failed: " + e.Message;
                return null;
            }
        }

        public static bool ConfigureSprite(string assetPath, SpriteImportOptions options, out string error)
        {
            error = null;
            string full;
            error = Permissions.CheckWrite(assetPath, out full, false, true);
            if (error != null) return false;

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) { error = "Not a texture asset: " + assetPath; return false; }

            try
            {
                (options ?? new SpriteImportOptions()).ApplyTo(importer);
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
                BridgeLog.Success("assets", "Updated import settings for " + assetPath);
                return true;
            }
            catch (Exception e)
            {
                error = "Could not update import settings: " + e.Message;
                return false;
            }
        }

        public static bool SliceGrid(string assetPath, int rows, int columns, out string error)
        {
            error = null;
            string full;
            error = Permissions.CheckWrite(assetPath, out full, false, true);
            if (error != null) return false;
            if (rows <= 0 || columns <= 0 || rows > 64 || columns > 64)
            {
                error = "Grid must be between 1x1 and 64x64.";
                return false;
            }

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) { error = "Not a texture asset: " + assetPath; return false; }

            try
            {
                int width, height;
                importer.GetSourceTextureWidthAndHeight(out width, out height);
                if (width <= 0 || height <= 0)
                {
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (texture == null) { error = "Could not determine texture dimensions."; return false; }
                    width = texture.width;
                    height = texture.height;
                }

                int cellWidth = Mathf.Max(1, width / columns);
                int cellHeight = Mathf.Max(1, height / rows);
                string baseName = Path.GetFileNameWithoutExtension(assetPath);
                var sprites = new List<SpriteMetaData>();
                for (int row = 0; row < rows; row++)
                {
                    for (int column = 0; column < columns; column++)
                    {
                        sprites.Add(new SpriteMetaData
                        {
                            name = baseName + "_" + row + "_" + column,
                            rect = new Rect(column * cellWidth, height - (row + 1) * cellHeight, cellWidth, cellHeight),
                            alignment = (int)SpriteAlignment.Center,
                            pivot = new Vector2(0.5f, 0.5f)
                        });
                    }
                }

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Multiple;
#pragma warning disable 0618
                // TextureImporter.spritesheet is deprecated in Unity 6 but remains the only
                // grid-slicing API available without the optional com.unity.2d.sprite package.
                // Verified working in 6000.6.7f1 by the self tests (16 sprites from a 4x4 grid).
                importer.spritesheet = sprites.ToArray();
#pragma warning restore 0618
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
                BridgeLog.Success("assets", "Sliced " + assetPath + " into " + sprites.Count + " sprites.");
                return true;
            }
            catch (Exception e)
            {
                error = "Slicing failed: " + e.Message;
                return false;
            }
        }

        public static string CreateMaterial(string assetPath, string shaderName, Color color, string texturePath, out string error)
        {
            error = null;
            string full;
            error = Permissions.CheckWrite(assetPath, out full, false, true);
            if (error != null) return null;
            if (!assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                error = "Material path must end with .mat";
                return null;
            }
            if (File.Exists(full))
            {
                error = "A material already exists at " + assetPath + ". It was not overwritten.";
                return null;
            }

            try
            {
                string folder = Path.GetDirectoryName(assetPath).Replace('\\', '/');
                EnsureAssetFolder(folder);

                Shader shader = null;
                if (!string.IsNullOrEmpty(shaderName)) shader = Shader.Find(shaderName);

                var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                bool universal = pipeline != null && pipeline.GetType().FullName != null &&
                                 pipeline.GetType().FullName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0;

                if (shader == null && universal)
                {
                    shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null) shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
                }
                if (shader == null) shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    error = "No suitable shader found. Specify a shader explicitly or configure the render pipeline.";
                    return null;
                }

                var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(assetPath) };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                else if (material.HasProperty("_Color")) material.SetColor("_Color", color);

                if (!string.IsNullOrEmpty(texturePath))
                {
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                    if (texture == null) BridgeLog.Warn("assets", "Texture not found for material: " + texturePath);
                    else
                    {
                        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                        else if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
                    }
                }

                AssetDatabase.CreateAsset(material, assetPath);
                AssetDatabase.SaveAssets();
                BridgeLog.Success("assets", "Created material " + assetPath + " using " + shader.name);
                return assetPath;
            }
            catch (Exception e)
            {
                error = "Could not create material: " + e.Message;
                return null;
            }
        }

        public static Sprite LoadSprite(string assetPath, out string error)
        {
            error = null;
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (sprite != null) return sprite;
            var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            foreach (var asset in assets)
            {
                sprite = asset as Sprite;
                if (sprite != null) return sprite;
            }
            error = "No Sprite found at " + assetPath + ". Configure the texture as a Sprite first.";
            return null;
        }

        public static bool AssignSprite(string targetPath, string componentKind, string spritePath, out string error)
        {
            error = null;
            var go = SceneService.FindByPath(targetPath);
            if (go == null) { error = "GameObject not found in the active scene: " + targetPath; return false; }

            var sprite = LoadSprite(spritePath, out error);
            if (sprite == null) return false;

            if (string.Equals(componentKind, "Image", StringComparison.OrdinalIgnoreCase))
            {
                SceneService.EnsureCanvas(go);
                var image = go.GetComponent<Image>();
                if (image == null) image = Undo.AddComponent<Image>(go);
                Undo.RecordObject(image, "Unity AI Bridge: assign sprite");
                image.sprite = sprite;
                image.preserveAspect = true;
                var rect = go.GetComponent<RectTransform>();
                if (rect != null && rect.sizeDelta == new Vector2(100f, 100f))
                    rect.sizeDelta = sprite.rect.size;
            }
            else
            {
                var renderer = go.GetComponent<SpriteRenderer>();
                if (renderer == null) renderer = Undo.AddComponent<SpriteRenderer>(go);
                Undo.RecordObject(renderer, "Unity AI Bridge: assign sprite");
                renderer.sprite = sprite;
            }

            EditorUtility.SetDirty(go);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            BridgeLog.Success("assets", "Assigned sprite " + sprite.name + " to " + targetPath);
            return true;
        }

        public static JsonValue InspectAsset(string assetPath)
        {
            string full;
            string error = Permissions.CheckRead(assetPath, out full);
            if (error != null) throw new BridgeException(error);

            var result = JsonValue.NewObject();
            result["path"] = assetPath;
            result["exists"] = File.Exists(full) || Directory.Exists(full);

            if (Directory.Exists(full))
            {
                result["kind"] = "folder";
                int files = 0, folders = 0;
                try
                {
                    files = Directory.GetFiles(full).Length;
                    folders = Directory.GetDirectories(full).Length;
                }
                catch { }
                result["files"] = files;
                result["subfolders"] = folders;
                result["guid"] = AssetDatabase.AssetPathToGUID(assetPath);
                return result;
            }

            if (!File.Exists(full)) throw new BridgeException("Asset not found: " + assetPath);

            var info = new FileInfo(full);
            result["size_bytes"] = info.Length;
            result["guid"] = AssetDatabase.AssetPathToGUID(assetPath);
            result["extension"] = info.Extension;

            var main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            result["type"] = main != null ? main.GetType().Name : "(unknown)";
            var importer = AssetImporter.GetAtPath(assetPath);
            result["importer"] = importer != null ? importer.GetType().Name : "(none)";

            var textureImporter = importer as TextureImporter;
            if (textureImporter != null)
            {
                result["texture_type"] = textureImporter.textureType.ToString();
                result["sprite_mode"] = textureImporter.spriteImportMode.ToString();
                result["pixels_per_unit"] = textureImporter.spritePixelsPerUnit;
                result["filter"] = textureImporter.filterMode.ToString();
                result["compression"] = textureImporter.textureCompression.ToString();
                result["max_size"] = textureImporter.maxTextureSize;
                result["alpha_is_transparency"] = textureImporter.alphaIsTransparency;
                result["mipmaps"] = textureImporter.mipmapEnabled;
                result["readable"] = textureImporter.isReadable;

                int width, height;
                try { textureImporter.GetSourceTextureWidthAndHeight(out width, out height); }
                catch { width = 0; height = 0; }
                if (width == 0 || height == 0)
                {
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (texture != null) { width = texture.width; height = texture.height; }
                }
                result["width"] = width;
                result["height"] = height;

                var sprites = JsonValue.NewArray();
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    var sprite = asset as Sprite;
                    if (sprite == null) continue;
                    sprites.Add(JsonValue.Obj(
                        "name", sprite.name,
                        "rect", JsonValue.Arr(sprite.rect.x, sprite.rect.y, sprite.rect.width, sprite.rect.height),
                        "pixels_per_unit", sprite.pixelsPerUnit));
                }
                result["sprites"] = sprites;
            }

            var prefab = main as GameObject;
            if (prefab != null)
            {
                result["kind"] = "prefab";
                result["root"] = prefab.name;
                var components = JsonValue.NewArray();
                foreach (var component in prefab.GetComponents<Component>())
                    if (component != null) components.Add(component.GetType().Name);
                result["components"] = components;
                result["child_count"] = prefab.transform.childCount;
            }

            var material = main as Material;
            if (material != null)
            {
                result["kind"] = "material";
                result["shader"] = material.shader != null ? material.shader.name : "(missing)";
                var properties = JsonValue.NewArray();
                if (material.shader != null)
                {
                    int count = material.shader.GetPropertyCount();
                    for (int i = 0; i < count; i++)
                    {
                        string propertyName = material.shader.GetPropertyName(i);
                        properties.Add(JsonValue.Obj(
                            "name", propertyName,
                            "type", material.shader.GetPropertyType(i).ToString()));
                    }
                }
                result["properties"] = properties;
            }

            var controller = main as AnimatorController;
            if (controller != null)
            {
                result["kind"] = "animator_controller";
                var layers = JsonValue.NewArray();
                foreach (var layer in controller.layers)
                {
                    var states = JsonValue.NewArray();
                    if (layer.stateMachine != null)
                        foreach (var child in layer.stateMachine.states)
                            states.Add(child.state != null ? child.state.name : "(state)");
                    layers.Add(JsonValue.Obj("name", layer.name, "states", states));
                }
                result["layers"] = layers;
            }

            if (result["kind"].IsNull) result["kind"] = "asset";
            return result;
        }

        public static List<string> FindSprites(string folder, int max = 100)
        {
            var result = new List<string>();
            string root = string.IsNullOrEmpty(folder) ? "Assets" : folder.TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(root) && root != "Assets") return result;
            string[] guids;
            try { guids = AssetDatabase.FindAssets("t:Sprite", new[] { root }); }
            catch { return result; }
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                result.Add(path);
                if (result.Count >= max) break;
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        public static JsonValue ListAssets(string folder, string filter, int max = 200)
        {
            string full;
            string error = Permissions.CheckRead(folder, out full);
            if (error != null) throw new BridgeException(error);

            var array = JsonValue.NewArray();
            string[] guids;
            try { guids = AssetDatabase.FindAssets(string.IsNullOrEmpty(filter) ? string.Empty : filter, new[] { folder }); }
            catch (Exception e) { throw new BridgeException(e.Message); }

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || Directory.Exists(path)) continue;
                if (!FileService.LooksLikeText(Path.GetExtension(path)) && !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                    continue;
                array.Add(JsonValue.Obj("path", path, "guid", guid));
                if (array.Count >= max) break;
            }
            return array;
        }
    }
}
