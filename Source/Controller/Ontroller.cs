using System.Runtime.InteropServices;

using Windows.Win32.Foundation;

using static Windows.Win32.PInvoke;

namespace Mu3IO;

public sealed class Ontroller : WinUsb, IController
{
    public const ushort VendorId  = 0x0E8F;
    public const ushort ProductId = 0x1216;

    private readonly byte[] _mu3LedState = new byte[33];
    private readonly int _lever_min, _lever_max;
    private readonly float _lever_absolute_center, _lever_range_center;

    public Ontroller(Device device)
        : base(device, VendorId, ProductId)
    {
        _lever_min = GetPrivateProfileInt("io4", "ontrollerLeverMin", 100, Mu3IO.ConfigFileName);
        _lever_max = GetPrivateProfileInt("io4", "ontrollerLeverMax", 600, Mu3IO.ConfigFileName);
        _lever_absolute_center = (_lever_max + _lever_min) / 2f; 
        _lever_range_center = (_lever_max - _lever_min) / 2f;

        Logger.Debug($"Ontroller: Connected!");
    }

    public override unsafe bool ReadInputData(out byte[] buffer, out int transferred)
    {
        // Prepare the buffer
        buffer = new byte[8];
        uint nTransferred = 0;

        // Retrieve the buffer w/ the actual buffer transfer count
        bool success = WinUsb_ReadPipe(UsbHandle, 0x84, buffer, &nTransferred, null);
        transferred = (int)nTransferred;

        // Validate device buffer as well
        success = success && buffer[0] == 0x44 && buffer[1] == 0x44 && buffer[2] == 0x54;
        if (!success)
        {
            // NO_ERROR means signature from Ontroller
            var errorCode = (WIN32_ERROR)Marshal.GetLastWin32Error();
            Logger.Debug($"Ontroller: Failed to pool the input data ({errorCode})");
        }

        return success;
    }

    public override unsafe bool WriteOutputData(byte[] buffer, out int transferred)
    {
        uint nTransferred = 0;
        bool success = WinUsb_WritePipe(UsbHandle, 0x03, buffer, &nTransferred, null);

        transferred = (int)nTransferred;
        return success;
    }

    public bool Poll()
    {
        // Clear states
        OptionButtonsFlag    = 0;
        LeftGameButtonsFlag  = 0;
        RightGameButtonsFlag = 0;

        // Poll input data
        if (!ReadInputData(out byte[] buffer, out int _))
            return false;

        // Map data into flags
        if ((buffer[3] & 0x20) != 0)
            LeftGameButtonsFlag |= 0x01;
        if ((buffer[3] & 0x10) != 0)
            LeftGameButtonsFlag |= 0x02;
        if ((buffer[3] & 0x08) != 0)
            LeftGameButtonsFlag |= 0x04;
        if ((buffer[3] & 0x04) != 0)
            RightGameButtonsFlag |= 0x01;
        if ((buffer[3] & 0x02) != 0)
            RightGameButtonsFlag |= 0x02;
        if ((buffer[3] & 0x01) != 0)
            RightGameButtonsFlag |= 0x04;
        if ((buffer[4] & 0x80) != 0)
            LeftGameButtonsFlag |= 0x08;
        if ((buffer[4] & 0x40) != 0)
            RightGameButtonsFlag |= 0x08;
        if ((buffer[4] & 0x20) != 0)
            LeftGameButtonsFlag |= 0x10;
        if ((buffer[4] & 0x10) != 0)
            RightGameButtonsFlag |= 0x10;
        if ((buffer[4] & 0x08) != 0)
            OptionButtonsFlag |= 0x01;
        if ((buffer[4] & 0x04) != 0)
            OptionButtonsFlag |= 0x02;

        // Read the lever data
        ushort leverValue = BitConverter.ToUInt16([buffer[6], buffer[5]]);  
        // Press both option buttons to log current lever value
        if (OptionButtonsFlag == 3)
            Logger.Debug($"Ontroller Lever Value: ({leverValue})");

        // The data fetched from WinUSB should be between 100 ~ 600 in big endian byte order
        // The value might be goes out of the bound occasionally due to the nature of rotary encoder (e.g 99 or 602, etc)
        ushort dataPosition      = Math.Clamp(leverValue, (ushort)_lever_min, (ushort)_lever_max);
        float normalizedPosition = (dataPosition - _lever_absolute_center) / _lever_range_center; // normalize the value between -1 ~ 1
        short mu3LeverPos        = (short)(short.MaxValue * normalizedPosition); // the upper-bound is short.MaxValue

        LeverPosition = mu3LeverPos;

        // Let's refresh the LEDs each time we poll the controller
        refreshLeds();

        return true;
    }

    public byte OptionButtonsFlag { get; private set; }

    public byte LeftGameButtonsFlag { get; private set; }

    public byte RightGameButtonsFlag { get; private set; }

    public short LeverPosition { get; private set; }

    public bool InitLeds()
    {
        // Not sure if this can be omitted
        _mu3LedState[0] = 0x44;
        _mu3LedState[1] = 0x4C;
        _mu3LedState[2] = 1;

        bool initLedsSuccessResult = WriteOutputData(_mu3LedState, out _);
        return initLedsSuccessResult;
    }

    public bool SetLeds(int board, byte[] ledsColors)
    {
        //Logger.Debug($"Ontroller: Setting leds color of board {board}...");

        // ; Data output a sequence of bytes, with JVS-like framing.
        // ; Each "packet" starts with 0xE0 as a sync. To avoid E0 appearing elsewhere,
        // ; 0xD0 is used as an escape character -- if you receive D0 in the output, ignore
        // ; it and use the next sent byte plus one instead.
        // ;
        // ; After the sync is one byte for the board number that was updated, followed by
        // ; the red, green and blue values for each LED.
        // ;
        // ; Board 0 has 61 LEDs:
        // ;   [0]-[1]: left side button
        // ;   [2]-[8]: left pillar lower LEDs
        // ;   [9]-[17]: left pillar center LEDs
        // ;   [18]-[24]: left pillar upper LEDs
        // ;   [25]-[35]: billboard LEDs
        // ;   [36]-[42]: right pillar upper LEDs
        // ;   [43]-[51]: right pillar center LEDs
        // ;   [52]-[58]: right pillar lower LEDs
        // ;   [59]-[60]: right side button
        // ;
        // ; Board 1 has 6 LEDs:
        // ;   [0]-[5]: 3 left and 3 right controller buttons

        if (board == 1)
        {
            // Setting the middle 6 LEDs 
            for (int i = 0; i <= 5; i++)
            {
                _mu3LedState[3 * i + 3] = ledsColors[i * 3]; // Red
                _mu3LedState[3 * i + 4] = ledsColors[i * 3 + 1]; // Green
                _mu3LedState[3 * i + 5] = ledsColors[i * 3 + 2]; // Blue
            }
        }
        else
        if (board == 0)
        {
            //Setting the left side LED

            _mu3LedState[3 * 6 + 3] = ledsColors[0]; // Red
            _mu3LedState[3 * 6 + 4] = ledsColors[1]; // Green
            _mu3LedState[3 * 6 + 5] = ledsColors[2]; // Blue

            //Setting the right side LED

            _mu3LedState[3 * 9 + 3] = ledsColors[61 * 3 - 3]; // Red
            _mu3LedState[3 * 9 + 4] = ledsColors[61 * 3 - 2]; // Green
            _mu3LedState[3 * 9 + 5] = ledsColors[61 * 3 - 1]; // Blue
        }

        return refreshLeds();
    }

    public bool refreshLeds()
    {
        return WriteOutputData(_mu3LedState, out _);
    }
}