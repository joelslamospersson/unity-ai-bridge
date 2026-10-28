using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAIBridge
{
    public sealed class ImageGenerationWindow : EditorWindow
    {
        TextField _prompt, _path, _model, _key;
        DropdownField _size, _quality, _background;
        Toggle _enabled, _consent;
        Button _generate, _stop;
        Label _status;
        Image _preview;
        Texture2D _texture;
        string _retainedPreview;
        Button _importPreview;
        CancellationTokenSource _cancellation;
        bool _busy;
        double _started;

        [MenuItem("Tools/Unity AI Bridge/Generate Image", false, 15)]
        public static void OpenWindow()
        {
            var window = GetWindow<ImageGenerationWindow>();
            window.titleContent = new GUIContent("Generate Image");
            window.minSize = new Vector2(440, 580);
            window.Show();
        }

        void CreateGUI()
        {
            rootVisualElement.Clear();
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            scroll.style.paddingLeft = scroll.style.paddingRight = 12;
            scroll.style.paddingTop = scroll.style.paddingBottom = 10;
            rootVisualElement.Add(scroll);
            var billing = new Label("Generate artwork with OpenAI API billing. Your saved API key is used; ChatGPT plan image credits are separate.");
            billing.style.whiteSpace = WhiteSpace.Normal; scroll.Add(billing);
            _enabled = new Toggle("Enable API image generation") { name = "image-generation-enabled", value = BridgeSettings.Current.ImageGenerationEnabled };
            _enabled.RegisterValueChangedCallback(e => { BridgeSettings.Current.ImageGenerationEnabled = e.newValue; BridgeSettings.Save(); UpdateButtons(); }); scroll.Add(_enabled);
            _key = new TextField("OpenAI API key") { isPasswordField = true, name = "image-api-key" }; scroll.Add(_key);
            var saveKey = new Button(() =>
            {
                if (string.IsNullOrWhiteSpace(_key.value)) return;
                BridgeSettings.Secrets.OpenAiApiKey = _key.value.Trim();
                BridgeSettings.SaveSecrets(); _key.SetValueWithoutNotify("");
                _status.text = "API key saved outside the project."; UpdateButtons();
            }) { text = "Save API key", name = "image-save-key" }; scroll.Add(saveKey);
            _model = new TextField("Image model") { value = BridgeSettings.Current.ImageGenerationModel, name = "image-model" }; scroll.Add(_model);
            _prompt = new TextField("Describe the artwork") { multiline = true, name = "image-prompt" };
            _prompt.style.minHeight = 100; _prompt.style.marginTop = 8; scroll.Add(_prompt);
            _path = new TextField("New PNG destination") { value = "Assets/Art/Generated/Image.png", name = "image-path" }; scroll.Add(_path);
            _size = new DropdownField("Size", new List<string> { "1024x1024", "1536x1024", "1024x1536" }, 0); scroll.Add(_size);
            _quality = new DropdownField("Quality", new List<string> { "auto", "low", "medium", "high" }, 0); scroll.Add(_quality);
            _background = new DropdownField("Background", new List<string> { "transparent", "opaque", "auto" }, 0); scroll.Add(_background);
            _consent = new Toggle("I authorize API charges for one image") { name = "image-cost-consent" }; scroll.Add(_consent);
            _generate = new Button(Generate) { text = "Generate one image (API billing)", name = "image-generate" }; scroll.Add(_generate);
            _stop = new Button(() => { if (_cancellation != null) _cancellation.Cancel(); }) { text = "Stop", name = "image-stop" }; scroll.Add(_stop);
            _status = new Label { name = "image-status" }; _status.style.whiteSpace = WhiteSpace.Normal; scroll.Add(_status);
            _preview = new Image { name = "image-preview", scaleMode = ScaleMode.ScaleToFit };
            _preview.style.height = 250; _preview.style.display = DisplayStyle.None; scroll.Add(_preview);
            _importPreview = new Button(() =>
            {
                try
                {
                    var parameters = JsonValue.Obj("incoming_path", _retainedPreview, "create_only", true, "sprite_mode", "Single", "alpha_is_transparency", true);
                    var change = ChangeService.Stage(ChangeKinds.ImportSprite, "Import generated preview", _path.value, null, parameters, "image-generation", false);
                    _status.text = "Preview import is " + change.Status + ". No new generation was requested.";
                    _retainedPreview = null; UpdateButtons();
                }
                catch (Exception e) { _status.text = FileService.RedactSecrets(e.Message); }
            }) { text = "Stage this preview at the new destination", name = "image-import-preview" };
            _importPreview.style.display = DisplayStyle.None; scroll.Add(_importPreview);
            scroll.Add(new Button(() =>
            {
                UnityAIBridgeBootstrap.OpenWindow();
                foreach (var window in Resources.FindObjectsOfTypeAll<UnityAIBridgeWindow>()) window.ShowChanges();
            }) { text = "Review imported image in Changes" });
            _consent.RegisterValueChangedCallback(_ => UpdateButtons());
            _prompt.RegisterValueChangedCallback(_ => UpdateButtons());
            rootVisualElement.schedule.Execute(() =>
            {
                if (_busy) _status.text = "Generating image · " + (int)(EditorApplication.timeSinceStartup - _started) + "s · waiting for the image API";
            }).Every(250);
            UpdateButtons();
        }

        void UpdateButtons()
        {
            if (_generate == null) return;
            _generate.SetEnabled(!_busy && _enabled.value && _consent.value && !string.IsNullOrWhiteSpace(_prompt.value) && !string.IsNullOrWhiteSpace(BridgeSettings.Secrets.OpenAiApiKey));
            _stop.style.display = _busy ? DisplayStyle.Flex : DisplayStyle.None;
            _enabled.SetEnabled(!_busy); _model.SetEnabled(!_busy); _path.SetEnabled(!_busy);
            _size.SetEnabled(!_busy); _quality.SetEnabled(!_busy); _background.SetEnabled(!_busy); _key.SetEnabled(!_busy);
            _prompt.SetEnabled(!_busy); _consent.SetEnabled(!_busy);
            rootVisualElement.Q<Button>("image-save-key").SetEnabled(!_busy);
            if (_importPreview != null) { _importPreview.style.display = string.IsNullOrEmpty(_retainedPreview) ? DisplayStyle.None : DisplayStyle.Flex; _importPreview.SetEnabled(!_busy); }
        }

        void Generate()
        {
            if (_busy || !_consent.value) return;
            BridgeSettings.Current.ImageGenerationModel = _model.value.Trim(); BridgeSettings.Save();
            var args = JsonValue.Obj("prompt", _prompt.value, "path", _path.value, "size", _size.value, "quality", _quality.value, "background", _background.value);
            _busy = true; _started = EditorApplication.timeSinceStartup;
            _consent.SetValueWithoutNotify(false);
            var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
            UpdateButtons();
            _ = GenerateAsync(args, cancellation);
        }

        async Task GenerateAsync(JsonValue args, CancellationTokenSource cancellation)
        {
            try
            {
                var result = await ImageGenerationService.GenerateAndStageAsync(args, cancellation.Token, false, true).ConfigureAwait(false);
                MainThread.Run(() =>
                {
                    if (this == null) return;
                    var content = result["structuredContent"];
                    _status.text = content["message"].AsString();
                    string preview = content["preview_path"].AsString();
                    _retainedPreview = content["status"].AsString() == "GeneratedNeedsImport" ? preview : null;
                    if (!string.IsNullOrEmpty(preview) && File.Exists(preview))
                    {
                        if (_texture != null) DestroyImmediate(_texture);
                        _texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                        if (_texture.LoadImage(File.ReadAllBytes(preview))) { _preview.image = _texture; _preview.style.display = DisplayStyle.Flex; }
                    }
                });
            }
            catch (OperationCanceledException) { MainThread.Run(() => { if (this != null) _status.text = "Generation stopped. A request already processed by the provider may still be billed."; }); }
            catch (Exception e) { MainThread.Run(() => { if (this != null) _status.text = FileService.RedactSecrets(e.Message); }); }
            finally
            {
                MainThread.Run(() =>
                {
                    if (this != null) { _busy = false; _cancellation = null; UpdateButtons(); }
                    cancellation.Dispose();
                });
            }
        }

        void OnDisable()
        {
            if (_cancellation != null) _cancellation.Cancel();
            if (_texture != null) DestroyImmediate(_texture);
        }
    }
}
