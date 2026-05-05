using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;

namespace Phalanx;
public static class Input
{
    public enum KeyCode: int
    {
        // gamepad 0
        G0_Button_A, G0_Button_B, G0_Button_X, G0_Button_Y, G0_Button_LeftBumper, G0_Button_RightBumper, G0_Button_Back,
        G0_Button_Start, G0_Button_Home, G0_Button_LeftStick, G0_Button_RightStick,
        G0_DPad_Up, G0_DPad_Right, G0_DPad_Down, G0_DPad_Left,
        // gamepad 1
        G1_Button_A, G1_Button_B, G1_Button_X, G1_Button_Y, G1_Button_LeftBumper, G1_Button_RightBumper, G1_Button_Back,
        G1_Button_Start, G1_Button_Home, G1_Button_LeftStick, G1_Button_RightStick,
        G1_DPad_Up, G1_DPad_Right, G1_DPad_Down, G1_DPad_Left,
        // gamepad 2
        G2_Button_A, G2_Button_B,
        // keyboard
        Space, 
        G2_Button_X, G2_Button_Y, G2_Button_LeftBumper, G2_Button_RightBumper, G2_Button_Back, // gamepad 2
        G2_Button_Start, // gamepad 2
        Apostrophe,
        G2_Button_Home, G2_Button_LeftStick, G2_Button_RightStick, G2_DPad_Up,// gamepad 2
        Comma,
        Minus, Period, Slash, 
        Alpha0, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
        Click_Left, //mouse
        Semicolon,
        Click_Middle, //mouse
        Equal,
        Click_Right, Click_Button4, Click_Button5, //mouse
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        LeftBracket, BackSlash, RightBracket,
        Click_Button6, Click_Button7, //mouse
        GraveAccent,
        Click_Button8, Click_Button9, Click_Button10, Click_Button11, Click_Button12, //mouse
        G2_DPad_Right, G2_DPad_Down, G2_DPad_Left, // gamepad 2
        // gamepad 3
        G3_Button_A, G3_Button_B, G3_Button_X, G3_Button_Y, G3_Button_LeftBumper, G3_Button_RightBumper, G3_Button_Back,
        G3_Button_Start, G3_Button_Home, G3_Button_LeftStick, G3_Button_RightStick,
        G3_DPad_Up, G3_DPad_Right, G3_DPad_Down, G3_DPad_Left,
        // gamepad 4
        G4_Button_A, G4_Button_B, G4_Button_X, G4_Button_Y, G4_Button_LeftBumper, G4_Button_RightBumper, G4_Button_Back,
        G4_Button_Start, G4_Button_Home, G4_Button_LeftStick, G4_Button_RightStick,
        G4_DPad_Up, G4_DPad_Right, G4_DPad_Down, G4_DPad_Left,
        // gamepad 5
        G5_Button_A, G5_Button_B, G5_Button_X, G5_Button_Y, G5_Button_LeftBumper, G5_Button_RightBumper, G5_Button_Back,
        G5_Button_Start, G5_Button_Home, G5_Button_LeftStick, G5_Button_RightStick,
        G5_DPad_Up, G5_DPad_Right, G5_DPad_Down, G5_DPad_Left,
        // gamepad 6
        G6_Button_A, G6_Button_B, G6_Button_X, G6_Button_Y, G6_Button_LeftBumper, G6_Button_RightBumper, G6_Button_Back,
        G6_Button_Start, G6_Button_Home, G6_Button_LeftStick, G6_Button_RightStick,
        World1, World2, //keyboard
        G6_DPad_Up, G6_DPad_Right, G6_DPad_Down, G6_DPad_Left,
        // gamepad 7
        G7_Button_A, G7_Button_B, G7_Button_X, G7_Button_Y, G7_Button_LeftBumper, G7_Button_RightBumper, G7_Button_Back,
        G7_Button_Start, G7_Button_Home, G7_Button_LeftStick, G7_Button_RightStick,
        G7_DPad_Up, G7_DPad_Right, G7_DPad_Down, G7_DPad_Left,
        // gamepad 8
        G8_Button_A, G8_Button_B, G8_Button_X, G8_Button_Y, G8_Button_LeftBumper, G8_Button_RightBumper, G8_Button_Back,
        G8_Button_Start, G8_Button_Home, G8_Button_LeftStick, G8_Button_RightStick,
        G8_DPad_Up, G8_DPad_Right, G8_DPad_Down, G8_DPad_Left,
        // gamepad 9
        G9_Button_A, G9_Button_B, G9_Button_X, G9_Button_Y, G9_Button_LeftBumper, G9_Button_RightBumper, G9_Button_Back,
        G9_Button_Start, G9_Button_Home, G9_Button_LeftStick, G9_Button_RightStick,
        G9_DPad_Up, G9_DPad_Right, G9_DPad_Down, G9_DPad_Left,
        // gamepad 10
        G10_Button_A, G10_Button_B, G10_Button_X, G10_Button_Y, G10_Button_LeftBumper, G10_Button_RightBumper, G10_Button_Back,
        G10_Button_Start, G10_Button_Home, G10_Button_LeftStick, G10_Button_RightStick,
        G10_DPad_Up, G10_DPad_Right, G10_DPad_Down, G10_DPad_Left,
        // gamepad 11
        G11_Button_A, G11_Button_B, G11_Button_X, G11_Button_Y, G11_Button_LeftBumper, G11_Button_RightBumper, G11_Button_Back,
        G11_Button_Start, G11_Button_Home, G11_Button_LeftStick, G11_Button_RightStick,
        G11_DPad_Up, G11_DPad_Right, G11_DPad_Down, G11_DPad_Left,
        // gamepad 12
        G12_Button_A, G12_Button_B, G12_Button_X, G12_Button_Y, G12_Button_LeftBumper, G12_Button_RightBumper, G12_Button_Back,
        G12_Button_Start, G12_Button_Home, G12_Button_LeftStick, G12_Button_RightStick,
        G12_DPad_Up, G12_DPad_Right, G12_DPad_Down,
        //keyboard
        Esc, Enter, Tab, Backspace, Insert, Delete,
        Arrow_Right, Arrow_Left, Arrow_Down, Arrow_Up,
        Page_Up, Page_Down, Home, End,
        G12_DPad_Left, // gamepad 12
        // gamepad 13
        G13_Button_A, G13_Button_B, G13_Button_X, G13_Button_Y, G13_Button_LeftBumper, G13_Button_RightBumper, G13_Button_Back,
        G13_Button_Start, G13_Button_Home,
        //keyboard
        CapsLock, ScrollLock, NumLock, PrintScreen, Pause,
        G13_Button_LeftStick, G13_Button_RightStick,
        G13_DPad_Up, G13_DPad_Right, G13_DPad_Down,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15,
        F16, F17, F18, F19, F20, F21, F22, F23, F24, F25,
        G13_DPad_Left, // gamepad 13
        // gamepad 14
        G14_Button_A, G14_Button_B, G14_Button_X, G14_Button_Y,
        //keyboard
        Keypad0, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
        KeypadDecimal, KeypadDivide, KeypadMultiply, KeypadSubtract, KeypadAdd, KeypadEnter, KeypadEqual,
        // gamepad 14
        G14_Button_LeftBumper, G14_Button_RightBumper, G14_Button_Back,
        //keyboard
        Shift_Left, Ctrl_Left, Alt_Left, Super_Left,
        Shift_Right, Ctrl_Right, Alt_Right, Super_Right,
        Menu,
        // gamepad 14
        G14_Button_Start, G14_Button_Home, G14_Button_LeftStick, G14_Button_RightStick,
        G14_DPad_Up, G14_DPad_Right, G14_DPad_Down, G14_DPad_Left,
        // gamepad 15
        G15_Button_A, G15_Button_B, G15_Button_X, G15_Button_Y, G15_Button_LeftBumper, G15_Button_RightBumper, G15_Button_Back,
        G15_Button_Start, G15_Button_Home, G15_Button_LeftStick, G15_Button_RightStick,
        G15_DPad_Up, G15_DPad_Right, G15_DPad_Down, G15_DPad_Left,
        KeyMax
    };
    public enum ControllerType
    {
        Gamepad,
        SteeringWheel,
        Max
    };
    public class Controller
    {
        public ControllerType Type { get; set; } = ControllerType.Max;
        public string Name { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
        public IGamepad? SilkGamepad { get; set; }
        public IJoystick? SilkJoystick { get; set; }
    }

    private const int KeyCount = (int)KeyCode.KeyMax;

    private static bool[] keys = new bool[KeyCount];
    private static bool[] keysPreviousFrame = new bool[KeyCount];

    private static IInputContext? inputContext;
    private static IKeyboard? keyboard;
    private static IMouse? mouse;

    private static Controller? gamepadController;
    private static Controller? steeringWheelController;

    private static HashSet<IInputDevice> knownDevices = new();
    private static int previousDeviceCount = -1;
    private static Vector2 mousePosition = Vector2.Zero;
    private static Vector2 mouseDelta = Vector2.Zero;
    private static Vector2 mouseWheelDelta = Vector2.Zero;

    public static bool[] GetKeys() { return keys; }
    public static Vector2 GetMousePosition() { return mousePosition; }
    public static Vector2 GetMouseDelta() { return mouseDelta; }
    public static Vector2 GetMouseWheelDelta() { return mouseWheelDelta; }

    public static void Initialize()
    {
        Array.Fill(keys, false);
        Array.Fill(keysPreviousFrame, false);

        inputContext = Window.GetWindow().CreateInput();

        keyboard = inputContext.Keyboards.Count > 0 ? inputContext.Keyboards[0] : null;
        mouse = inputContext.Mice.Count > 0 ? inputContext.Mice[0] : null;

        if (keyboard != null)
        {
            keyboard.KeyDown += (_, key, _) => UpdateKeyState(key, true);
            keyboard.KeyUp += (_, key, _) => UpdateKeyState(key, false);
        }
        if (mouse != null)
        {
            mouse.Scroll +=(_, scrollWheel) => UpdateScrollWheel(scrollWheel);
            mouse.MouseDown += (_, button) => UpdateMouseButton(button, true);
            mouse.MouseUp += (_, button) => UpdateMouseButton(button, false);
        }

        Log.LogInfo("Input system initialized (Silk.NET.Input)");
    }

    public static bool GetKey(KeyCode key)
    {
        return keys[(int)key];
    }
    private static void PollMouse()
    {
        if (mouse != null)
        {
            Vector2 currentPosition = new Vector2(mouse.Position.X, mouse.Position.Y);

            mouseDelta = currentPosition - mousePosition;
            mousePosition = currentPosition;
        }
    }
    private static void PollGamepads()
    {
        if (gamepadController?.SilkGamepad is IGamepad gp && gp.IsConnected)
        {

        }
    }

    private static void PollSteeringWheels()
    {
        if (steeringWheelController?.SilkJoystick is IJoystick js && js.IsConnected)
        {

        }
    }
    public static void Tick()
    {
        Array.Copy(keys, keysPreviousFrame, KeyCount);

        // Poll current device list and detect connect/disconnect
        DetectDeviceChanges();

        // Update button/axis states
        PollMouse();
        PollGamepads();
        PollSteeringWheels();
    }
    private static void UpdateKeyState(Key key, bool pressed)
    {
            keys[(int)key] = pressed;
    }

    private static void UpdateScrollWheel(ScrollWheel scrollWheel)
    {
        mouseWheelDelta.X += scrollWheel.X;
        mouseWheelDelta.Y += scrollWheel.Y;
    }
    private static void UpdateMouseButton(MouseButton button, bool pressed)
    {
        if (button == MouseButton.Left)
            keys[(int)KeyCode.Click_Left] = pressed;
        if (button == MouseButton.Middle)
            keys[(int)KeyCode.Click_Middle] = pressed;
        if (button == MouseButton.Right)
            keys[(int)KeyCode.Click_Right] = pressed;
    }

    private static void UpdateButtonState(Button button, bool pressed)
    {

    }
    private static void HandleDeviceAdded(IInputDevice device)
    {
        if (device is IGamepad gamepad)
        {
            gamepadController ??= new Controller { Type = ControllerType.Gamepad };
            gamepadController.IsConnected = true;
            gamepadController.Name = gamepad.Name;
            gamepadController.SilkGamepad = gamepad;
            gamepad.ButtonDown += (_, button) => UpdateButtonState(button, true);
            gamepad.ButtonUp += (_, button) => UpdateButtonState(button, false);

            Log.LogInfo($"Gamepad connected: \"{gamepad.Name}\"");
        }
        else if (device is IJoystick joystick)
        {
            bool isWheel = joystick.Name.Contains("wheel", StringComparison.OrdinalIgnoreCase) ||
                           joystick.Axes.Count >= 4; // rough heuristic

            if (isWheel)
            {
                steeringWheelController ??= new Controller { Type = ControllerType.SteeringWheel };
                steeringWheelController.IsConnected = true;
                steeringWheelController.Name = joystick.Name;
                steeringWheelController.SilkJoystick = joystick;
                joystick.ButtonDown += (_, button) => UpdateButtonState(button, true);
                joystick.ButtonUp += (_, button) => UpdateButtonState(button, false);
                Log.LogInfo($"Steering wheel connected: \"{joystick.Name}\"");
            }
            else
            {
                gamepadController ??= new Controller { Type = ControllerType.Gamepad };
                gamepadController.IsConnected = true;
                gamepadController.Name = joystick.Name;
                gamepadController.SilkJoystick = joystick;
                joystick.ButtonDown += (_, button) => UpdateButtonState(button, true);
                joystick.ButtonUp += (_, button) => UpdateButtonState(button, false);

                Log.LogInfo($"Joystick connected: \"{joystick.Name}\"");
            }
        }
    }

    private static void HandleDeviceRemoved(IInputDevice device)
    {
        if (gamepadController?.SilkGamepad == device)
        {
            Log.LogInfo($"Gamepad disconnected: \"{gamepadController.Name}\"");
            gamepadController.IsConnected = false;
            gamepadController.SilkGamepad = null;
        }
        else if (steeringWheelController?.SilkJoystick == device)
        {
            Log.LogInfo($"Steering wheel disconnected: \"{steeringWheelController.Name}\"");
            steeringWheelController.IsConnected = false;
            steeringWheelController.SilkJoystick = null;
        }
    }
    private static void DetectDeviceChanges()
    {
        if (inputContext == null)
            return;

        int currentCount = inputContext.Gamepads.Count + inputContext.Joysticks.Count;

        if (currentCount == previousDeviceCount)
            return;

        var currentDevices = new HashSet<IInputDevice>(inputContext.Gamepads.Cast<IInputDevice>().Concat(inputContext.Joysticks));

        // New devices
        foreach (var device in currentDevices)
        {
            if (knownDevices.Add(device))
            {
                HandleDeviceAdded(device);
            }
        }

        // Removed devices
        foreach (var device in knownDevices.ToList())
        {
            if (!currentDevices.Contains(device))
            {
                HandleDeviceRemoved(device);
                knownDevices.Remove(device);
            }
        }

        previousDeviceCount = currentCount;
    }
}