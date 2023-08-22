﻿// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2023, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.GameSystem
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using Command;
    using Command.InternalCommands;
    using Command.SceCommands;
    using Core.Animation;
    using Core.Extensions;
    using Core.Utils;
    using Data;
    using Input;
    using MetaData;
    using Scene;
    using Script.Waiter;
    using State;
    using TMPro;
    using UI;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.InputSystem;
    using UnityEngine.InputSystem.DualShock;
    using UnityEngine.UI;
    using Debug = UnityEngine.Debug;

    public sealed class DialogueManager : IDisposable,
        ICommandExecutor<DialogueRenderActorAvatarCommand>,
        ICommandExecutor<DialogueRenderTextCommand>,
        ICommandExecutor<DialogueAddSelectionsCommand>,
        ICommandExecutor<DialogueRenderTextWithTimeLimitCommand>,
        ICommandExecutor<ResetGameStateCommand>
    {
        private const float LIMIT_TIME_DIALOGUE_PLAYER_MAX_REACTION_TIME_IN_SECONDS = 4f;
        private const float DIALOGUE_SHOW_HIDE_ANIMATION_DURATION = 0.12f;
        private const float DIALOGUE_SHOW_HIDE_ANIMATION_Y_OFFSET = -30f;
        private const float DIALOGUE_FLASHING_ANIMATION_DURATION = 0.5f;

        private const string INFORMATION_TEXT_COLOR_HEX = "#ffff05";

        private readonly GameResourceProvider _resourceProvider;
        private readonly GameStateManager _gameStateManager;
        private readonly SceneManager _sceneManager;
        private readonly InputManager _inputManager;
        private readonly PlayerInputActions _inputActions;

        private readonly EventSystem _eventSystem;
        private readonly CanvasGroup _dialogueCanvasGroup;
        private readonly Canvas _dialogueSelectionButtonsCanvas;
        private readonly GameObject _dialogueSelectionButtonPrefab;
        private readonly Image _dialogueBackgroundImage;
        private readonly RoundedFrostedGlassImage _backgroundFrostedGlassImage;

        private readonly Image _avatarImageLeft;
        private readonly Image _avatarImageRight;
        private readonly TextMeshProUGUI _dialogueTextLeft;
        private readonly TextMeshProUGUI _dialogueTextRight;
        private readonly TextMeshProUGUI _dialogueTextDefault;

        private Texture2D _avatarTexture;
        private bool _isDialoguePresenting;
        private bool _isSkipDialogueRequested;
        private bool _isDialogueRenderingAnimationInProgress;

        private int _lastSelectedButtonIndex;
        private readonly List<GameObject> _selectionButtons = new();
        private double _totalTimeUsedBeforeSkippingTheLastDialogue;
        private readonly Stopwatch _reactionTimer = new ();
        private CancellationTokenSource _flashingAnimationCts = new ();

        private DialogueRenderActorAvatarCommand _lastAvatarCommand;
        private readonly Queue<IEnumerator> _dialogueRenderQueue = new();

        public DialogueManager(GameResourceProvider resourceProvider,
            GameStateManager gameStateManager,
            SceneManager sceneManager,
            InputManager inputManager,
            EventSystem eventSystem,
            CanvasGroup dialogueCanvasGroup,
            Image dialogueBackgroundImage,
            Image avatarImageLeft,
            Image avatarImageRight,
            TextMeshProUGUI textLeft,
            TextMeshProUGUI textRight,
            TextMeshProUGUI textDefault,
            Canvas dialogueSelectionButtonsCanvas,
            GameObject dialogueSelectionButtonPrefab)
        {
            _resourceProvider = Requires.IsNotNull(resourceProvider, nameof(resourceProvider));
            _gameStateManager = Requires.IsNotNull(gameStateManager, nameof(gameStateManager));
            _sceneManager = Requires.IsNotNull(sceneManager, nameof(sceneManager));
            _inputManager = Requires.IsNotNull(inputManager, nameof(inputManager));
            _eventSystem = Requires.IsNotNull(eventSystem, nameof(eventSystem));

            _dialogueCanvasGroup = Requires.IsNotNull(dialogueCanvasGroup, nameof(dialogueCanvasGroup));
            _dialogueBackgroundImage = Requires.IsNotNull(dialogueBackgroundImage, nameof(dialogueBackgroundImage));
            _backgroundFrostedGlassImage = Requires.IsNotNull(dialogueBackgroundImage.GetComponent<RoundedFrostedGlassImage>(),
                nameof(dialogueBackgroundImage));

            _avatarImageLeft = Requires.IsNotNull(avatarImageLeft, nameof(avatarImageLeft));
            _avatarImageRight = Requires.IsNotNull(avatarImageRight, nameof(avatarImageRight));

            _dialogueTextLeft = Requires.IsNotNull(textLeft, nameof(textLeft));
            _dialogueTextRight = Requires.IsNotNull(textRight, nameof(textRight));
            _dialogueTextDefault = Requires.IsNotNull(textDefault, nameof(textDefault));

            _dialogueSelectionButtonsCanvas = Requires.IsNotNull(dialogueSelectionButtonsCanvas, nameof(dialogueSelectionButtonsCanvas));
            _dialogueSelectionButtonPrefab = Requires.IsNotNull(dialogueSelectionButtonPrefab, nameof(dialogueSelectionButtonPrefab));

            _avatarImageLeft.preserveAspect = true;
            _avatarImageRight.preserveAspect = true;

            ResetUI();

            _inputActions = inputManager.GetPlayerInputActions();
            _inputActions.Cutscene.Continue.performed += SkipDialoguePerformed;

            CommandExecutorRegistry<ICommand>.Instance.Register(this);
        }

        public void Dispose()
        {
            CommandExecutorRegistry<ICommand>.Instance.UnRegister(this);
            _inputActions.Cutscene.Continue.performed -= SkipDialoguePerformed;
        }

        public int GetDialogueSelectionButtonIndex()
        {
            return _lastSelectedButtonIndex;
        }

        public bool PlayerReactedInTimeForLimitTimeDialogue()
        {
            return _totalTimeUsedBeforeSkippingTheLastDialogue < LIMIT_TIME_DIALOGUE_PLAYER_MAX_REACTION_TIME_IN_SECONDS;
        }

        private IEnumerator TypeSentenceAsync(TextMeshProUGUI textUI, string sentence, float waitSecondsBeforeRenderingChar)
        {
            var charTypingAnimationDelay = new WaitForSeconds(waitSecondsBeforeRenderingChar);

            textUI.text = string.Empty;

            yield return charTypingAnimationDelay;

            var richText = string.Empty;
            foreach (var letter in sentence.ToCharArray())
            {
                if (richText.Length > 0)
                {
                    if (letter.Equals('>') && richText.Contains('>'))
                    {
                        textUI.text += richText + letter;
                        richText = string.Empty;
                        yield return charTypingAnimationDelay;
                        continue;
                    }

                    richText += letter;
                    continue;
                }
                else if (letter.Equals('<'))
                {
                    richText += letter;
                    continue;
                }

                if (_isDialogueRenderingAnimationInProgress == false) yield break;
                textUI.text += letter;
                yield return charTypingAnimationDelay;
            }
        }

        public void Update(float deltaTime)
        {
            if (!_isDialoguePresenting && _dialogueRenderQueue.Count > 0)
            {
                _isDialoguePresenting = true;
               Pal3.Instance.StartCoroutine(_dialogueRenderQueue.Dequeue());
            }
        }

        private TextMeshProUGUI GetRenderingTextUI(bool isAvatarPresented, bool isRightAligned)
        {
            if (!isAvatarPresented) return _dialogueTextDefault;
            else return isRightAligned ? _dialogueTextRight : _dialogueTextLeft;
        }

        private IEnumerator RenderDialogueTextWithAnimationAsync(TextMeshProUGUI dialogueTextUI,
            string text,
            float waitSecondsBeforeRenderingChar = 0.04f)
        {
            if (waitSecondsBeforeRenderingChar < Mathf.Epsilon)
            {
                dialogueTextUI.text = text;
            }
            else
            {
                _isDialogueRenderingAnimationInProgress = true;
                yield return null;
                yield return TypeSentenceAsync(dialogueTextUI, text, waitSecondsBeforeRenderingChar);
            }

            _isDialogueRenderingAnimationInProgress = false;
        }

        /// <summary>
        /// Break long dialogue into pieces
        /// Basically separate a dialogue into two pieces if there are more
        /// than three new line chars found in the dialogue text.
        /// </summary>
        /// <param name="text"></param>
        /// <returns>One or two sub dialogues</returns>
        private IEnumerable<string> GetSubDialoguesAsync(string text)
        {
            if (text.Contains('\n'))
            {
                var indexOfSecondNewLineChar = text.IndexOf('\n', text.IndexOf('\n') + 1);
                if (indexOfSecondNewLineChar != -1)
                {
                    var indexOfThirdNewLineChar = text.IndexOf('\n', indexOfSecondNewLineChar + 1);
                    if (indexOfThirdNewLineChar != -1 && indexOfThirdNewLineChar != text.Length)
                    {
                        var firstPart = text.Substring(0, indexOfThirdNewLineChar);
                        var secondPart = text.Substring(indexOfThirdNewLineChar, text.Length - indexOfThirdNewLineChar);
                        yield return firstPart;
                        yield return text.Substring(0, text.IndexOf('\n')) + secondPart;
                        yield break;
                    }
                }
            }

            yield return text;
        }

        private IEnumerator RenderDialogueAndWaitAsync(string text,
            bool trackReactionTime,
            DialogueRenderActorAvatarCommand avatarCommand = null,
            Action onFinished = null)
        {
            CommandDispatcher<ICommand>.Instance.Dispatch(new DialogueRenderingStartedNotification());
            _totalTimeUsedBeforeSkippingTheLastDialogue = 0f;

            bool isRightAligned = true;
            bool isAvatarPresented = false;

            if (avatarCommand != null &&
                _sceneManager.GetCurrentScene().GetActor(avatarCommand.ActorId) is { } actor &&
                _resourceProvider.GetActorAvatarSprite(actor.Info.Name, avatarCommand.AvatarTextureName) is { } avatarSprite)
            {
                isRightAligned = avatarCommand.RightAligned == 1;

                if (isRightAligned)
                {
                    _avatarImageRight.color = Color.white;
                    _avatarImageRight.sprite = avatarSprite;
                }
                else
                {
                    _avatarImageLeft.color = Color.white;
                    _avatarImageLeft.sprite = avatarSprite;
                }

                isAvatarPresented = true;
            }

            TextMeshProUGUI dialogueTextUI = GetRenderingTextUI(isAvatarPresented, isRightAligned);

            _dialogueBackgroundImage.enabled = true;
            _dialogueCanvasGroup.alpha = 0f;
            _dialogueCanvasGroup.enabled = true;
            _isSkipDialogueRequested = false;

            yield return PlayDialogueBackgroundPopAnimationAsync(true);
            _isSkipDialogueRequested = false; // Ignore skip request during dialogue rendering animation

            if (trackReactionTime) // Setup timer and start flashing animation
            {
                _reactionTimer.Restart();
                if (!_flashingAnimationCts.IsCancellationRequested)
                {
                    _flashingAnimationCts.Cancel();
                }
                _flashingAnimationCts = new CancellationTokenSource();
                Pal3.Instance.StartCoroutine(PlayDialogueBackgroundFlashingAnimationAsync(
                     duration: LIMIT_TIME_DIALOGUE_PLAYER_MAX_REACTION_TIME_IN_SECONDS,
                    _flashingAnimationCts.Token));
            }

            // Render dialogue text typing animation
            foreach (var dialogue in GetSubDialoguesAsync(text))
            {
                IEnumerator renderDialogue = RenderDialogueTextWithAnimationAsync(dialogueTextUI, dialogue);

                Pal3.Instance.StartCoroutine(renderDialogue);

                yield return SkipDialogueRequestedAsync();

                if (_isDialogueRenderingAnimationInProgress)
                {
                    _isDialogueRenderingAnimationInProgress = false;
                    Pal3.Instance.StopCoroutine(renderDialogue);
                    dialogueTextUI.text = dialogue;
                    yield return SkipDialogueRequestedAsync();
                }
            }

            if (trackReactionTime) // Stop flashing animation and timer
            {
                _flashingAnimationCts.Cancel();
                _reactionTimer.Stop();
                _totalTimeUsedBeforeSkippingTheLastDialogue = _reactionTimer.Elapsed.TotalSeconds;
                Debug.LogWarning($"[{nameof(DialogueManager)}] Reaction time: {_totalTimeUsedBeforeSkippingTheLastDialogue:0.00f}");
            }

            yield return PlayDialogueBackgroundPopAnimationAsync(false);

            ResetUI();

            _isDialoguePresenting = false;

            onFinished?.Invoke();
        }

        private IEnumerator PlayDialogueBackgroundFlashingAnimationAsync(float duration, CancellationToken cancellationToken)
        {
            float startTime = Time.realtimeSinceStartup;
            float initialBlurAmount = _backgroundFrostedGlassImage.blurAmount;
            float initialTransparency = _backgroundFrostedGlassImage.transparency;
            const float minThresholdPercentage = 0.65f;

            while (!cancellationToken.IsCancellationRequested)
            {
                yield return CoreAnimation.EnumerateValueAsync(1f, minThresholdPercentage, DIALOGUE_FLASHING_ANIMATION_DURATION / 2f,
                    AnimationCurveType.Linear, value =>
                    {
                        _backgroundFrostedGlassImage.SetMaterialBlurAmount(initialBlurAmount * value);
                        _backgroundFrostedGlassImage.SetMaterialTransparency(initialTransparency * value);
                    }, cancellationToken);

                if (Time.realtimeSinceStartup - startTime >= duration) break;

                yield return CoreAnimation.EnumerateValueAsync(minThresholdPercentage, 1f, DIALOGUE_FLASHING_ANIMATION_DURATION / 2f,
                    AnimationCurveType.Linear, value =>
                    {
                        _backgroundFrostedGlassImage.SetMaterialBlurAmount(initialBlurAmount * value);
                        _backgroundFrostedGlassImage.SetMaterialTransparency(initialTransparency * value);
                    }, cancellationToken);

                if (Time.realtimeSinceStartup - startTime >= duration) break;
            }

            _backgroundFrostedGlassImage.SetMaterialBlurAmount(initialBlurAmount);
            _backgroundFrostedGlassImage.SetMaterialTransparency(initialTransparency);
        }

        private IEnumerator PlayDialogueBackgroundPopAnimationAsync(bool showDialogue)
        {
            const float yOffset = DIALOGUE_SHOW_HIDE_ANIMATION_Y_OFFSET;
            Transform dialogueCanvasGroupTransform = _dialogueCanvasGroup.transform;
            Vector3 finalPosition = dialogueCanvasGroupTransform.localPosition;
            Vector3 startPosition = finalPosition + new Vector3(0f, yOffset, 0);

            float startValue = showDialogue ? 0f : 1f;
            float endValue = showDialogue ? 1f : 0f;

            if (showDialogue)
            {
                dialogueCanvasGroupTransform.localPosition = startPosition;
            }

            var initialBlurAmount = _backgroundFrostedGlassImage.blurAmount;
            var initialTransparency = _backgroundFrostedGlassImage.transparency;
            yield return CoreAnimation.EnumerateValueAsync(startValue, endValue, DIALOGUE_SHOW_HIDE_ANIMATION_DURATION,
                AnimationCurveType.EaseIn, value =>
                {
                    _dialogueCanvasGroup.transform.localPosition = finalPosition + new Vector3(0f, yOffset * (1 - value), 0);
                    _dialogueCanvasGroup.alpha = value;
                    _backgroundFrostedGlassImage.SetMaterialBlurAmount(initialBlurAmount * value);
                    _backgroundFrostedGlassImage.SetMaterialTransparency(initialTransparency * value);
                });
            _backgroundFrostedGlassImage.SetMaterialBlurAmount(initialBlurAmount);
            _backgroundFrostedGlassImage.SetMaterialTransparency(initialTransparency);

            _dialogueCanvasGroup.transform.localPosition = finalPosition; // Always set to final position
            _dialogueCanvasGroup.alpha = showDialogue ? 1f : 0f;
        }

        private void ResetUI()
        {
            _dialogueCanvasGroup.alpha = 0f;
            _dialogueCanvasGroup.enabled = false;

            _dialogueTextLeft.text = string.Empty;
            _dialogueTextRight.text = string.Empty;
            _dialogueTextDefault.text = string.Empty;

            _dialogueBackgroundImage.enabled = false;

            _avatarImageLeft.color = new Color(0f, 0f, 0f, 0f);
            _avatarImageRight.color = new Color(0f, 0f, 0f, 0f);
            _avatarImageLeft.sprite = null;
            _avatarImageRight.sprite = null;

            _dialogueSelectionButtonsCanvas.enabled = false;

            foreach (GameObject button in _selectionButtons)
            {
                button.GetComponentInChildren<Button>().onClick.RemoveAllListeners();
                button.Destroy();
            }
            _selectionButtons.Clear();
        }

        private IEnumerator SkipDialogueRequestedAsync()
        {
            yield return new WaitUntil(() => _isSkipDialogueRequested);
            _isSkipDialogueRequested = false;
        }

        private string GetDisplayText(string text)
        {
            var formattedText = text.Replace("\\n", "\n");

            return ReplaceStringWithPatternForEachChar(formattedText,
                "\\i", "\\r",
                $"<color={INFORMATION_TEXT_COLOR_HEX}>", "</color>");
        }

        private string ReplaceStringWithPatternForEachChar(string str,
            string startPattern,
            string endPattern,
            string charStartPattern,
            string charEndPattern)
        {
            var newStr = string.Empty;

            var currentIndex = 0;
            var startOfInformation = str.IndexOf(startPattern, StringComparison.Ordinal);
            while (startOfInformation != -1)
            {
                var endOfInformation = str.IndexOf(endPattern, startOfInformation, StringComparison.Ordinal);

                newStr += str.Substring(currentIndex, startOfInformation - currentIndex);

                foreach (var ch in str.Substring(
                             startOfInformation + startPattern.Length,
                             endOfInformation - startOfInformation - startPattern.Length))
                {
                    newStr += $"{charStartPattern}{ch}{charEndPattern}";
                }

                currentIndex = endOfInformation + endPattern.Length;
                startOfInformation = str.IndexOf(
                    startPattern, currentIndex, StringComparison.Ordinal);
            }

            newStr += str.Substring(currentIndex, str.Length - currentIndex);

            return newStr;
        }

        private void SkipDialoguePerformed(InputAction.CallbackContext _)
        {
            if (_dialogueSelectionButtonsCanvas.enabled) return;
            _isSkipDialogueRequested = true;
        }

        public void Execute(DialogueRenderTextCommand command)
        {
            var skipDialogueWaiter = new WaitUntilCanceled();
            CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(skipDialogueWaiter));
            DialogueRenderActorAvatarCommand avatarCommand = _lastAvatarCommand;
            _dialogueRenderQueue.Enqueue(RenderDialogueAndWaitAsync(
                GetDisplayText(command.DialogueText),
                false,
                avatarCommand,
                () => skipDialogueWaiter.CancelWait()));
            _lastAvatarCommand = null;
        }

        public void Execute(DialogueRenderTextWithTimeLimitCommand command)
        {
            var skipDialogueWaiter = new WaitUntilCanceled();
            CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(skipDialogueWaiter));
            DialogueRenderActorAvatarCommand avatarCommand = _lastAvatarCommand;
            _dialogueRenderQueue.Enqueue(RenderDialogueAndWaitAsync(
                GetDisplayText(command.DialogueText),
                true,
                avatarCommand,
                () => skipDialogueWaiter.CancelWait()));
            _lastAvatarCommand = null;
        }

        public void Execute(DialogueRenderActorAvatarCommand command)
        {
            if (command.ActorId == ActorConstants.PlayerActorVirtualID) return;
            _lastAvatarCommand = command;
        }

        private string GetSelectionDisplayText(object selection)
        {
            var selectionString = (string)selection;

            if (selectionString.EndsWith("；") || selectionString.EndsWith("。")) selectionString = selectionString[..^1];

            if (selectionString.Contains('.'))
            {
                var numberStr = selectionString[..selectionString.IndexOf('.')];
                if (int.TryParse(numberStr, out _))
                {
                    return selectionString[(selectionString.IndexOf('.') + 1)..];
                }
            }

            if (selectionString.Contains('、'))
            {
                var numberStr = selectionString[..selectionString.IndexOf('、')];
                if (int.TryParse(numberStr, out _))
                {
                    return selectionString[(selectionString.IndexOf('、') + 1)..];
                }
            }

            // I don't think there will be more than 20 options, so let's start with 20
            for (var i = 20; i >= 0; i--)
            {
                var intStr = i.ToString();
                if (selectionString.StartsWith(intStr) && !string.Equals(selectionString, intStr))
                {
                    return selectionString[intStr.Length..];
                }
            }

            return selectionString;
        }

        public void Execute(DialogueAddSelectionsCommand command)
        {
            _gameStateManager.TryGoToState(GameState.UI);

            WaitUntilCanceled waiter = new ();
            CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));

            Transform canvasTransform = _dialogueSelectionButtonsCanvas.transform;
            for (var i = 0; i < command.Selections.Count; i++)
            {
                GameObject selectionButton = UnityEngine.Object.Instantiate(_dialogueSelectionButtonPrefab, canvasTransform);
                var buttonTextUI = selectionButton.GetComponentInChildren<TextMeshProUGUI>();
                buttonTextUI.text = GetSelectionDisplayText(command.Selections[i]);
                var buttonIndex = i;
                var button = selectionButton.GetComponentInChildren<Button>();
                button.colors = UITheme.GetButtonColors();
                button.onClick
                    .AddListener(delegate
                    {
                        SelectionButtonClicked(buttonIndex);
                        waiter.CancelWait();
                        _gameStateManager.GoToPreviousState();
                    });
                _selectionButtons.Add(selectionButton);
            }

            // Setup button navigation
            void ConfigureButtonNavigation(Button button, int index, int count)
            {
                Navigation buttonNavigation = button.navigation;
                buttonNavigation.mode = Navigation.Mode.Explicit;

                int upIndex = index == 0 ? count - 1 : index - 1;
                int downIndex = index == count - 1 ? 0 : index + 1;

                buttonNavigation.selectOnUp = _selectionButtons[upIndex].GetComponentInChildren<Button>();
                buttonNavigation.selectOnDown = _selectionButtons[downIndex].GetComponentInChildren<Button>();

                button.navigation = buttonNavigation;
            }

            for (var i = 0; i < command.Selections.Count; i++)
            {
                var button = _selectionButtons[i].GetComponentInChildren<Button>();
                ConfigureButtonNavigation(button, i, command.Selections.Count);
            }

            var firstButton = _selectionButtons.First().GetComponentInChildren<Button>();

            InputDevice lastActiveInputDevice = _inputManager.GetLastActiveInputDevice();
            if (lastActiveInputDevice == Keyboard.current ||
                lastActiveInputDevice == Gamepad.current ||
                lastActiveInputDevice == DualShockGamepad.current)
            {
                _eventSystem.firstSelectedGameObject = firstButton.gameObject;
                firstButton.Select();
            }
            else
            {
                _eventSystem.firstSelectedGameObject = null;
            }

            _dialogueCanvasGroup.alpha = 1f;
            _dialogueCanvasGroup.enabled = true;
            _dialogueSelectionButtonsCanvas.enabled = true;
        }

        private void SelectionButtonClicked(int index)
        {
            _lastSelectedButtonIndex = index;
            ResetUI();
        }

        public void Execute(ResetGameStateCommand command)
        {
            _lastAvatarCommand = null;
            _totalTimeUsedBeforeSkippingTheLastDialogue = 0f;
            _dialogueRenderQueue.Clear();
            _reactionTimer?.Reset();

            if (!_flashingAnimationCts.IsCancellationRequested)
            {
                _flashingAnimationCts.Cancel();
                _flashingAnimationCts = new CancellationTokenSource();
            }

            ResetUI();
        }
    }
}