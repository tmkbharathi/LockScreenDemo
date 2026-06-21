using System;
using System.IO;

namespace LockScreenDemo.Shared
{
    public enum PacketType : byte
    {
        ScreenFrame = 1,
        MouseInput = 2,
        KeyboardInput = 3,
        ClipboardSync = 4,
        HostInfo = 5
    }

    public enum MouseMsgType : byte
    {
        Move = 0,
        LeftDown = 1,
        LeftUp = 2,
        RightDown = 3,
        RightUp = 4,
        Wheel = 5
    }

    public struct MousePacket
    {
        public MouseMsgType Type;
        public int X;
        public int Y;
        public int WheelDelta;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)Type);
                bw.Write(X);
                bw.Write(Y);
                bw.Write(WheelDelta);
                return ms.ToArray();
            }
        }

        public static MousePacket Deserialize(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                var packet = new MousePacket();
                packet.Type = (MouseMsgType)br.ReadByte();
                packet.X = br.ReadInt32();
                packet.Y = br.ReadInt32();
                packet.WheelDelta = br.ReadInt32();
                return packet;
            }
        }

        public const int Size = 13;
    }

    public struct KeyboardPacket
    {
        public ushort VirtualKeyCode;
        public ushort ScanCode;
        public uint Flags;
        public bool IsKeyUp;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(VirtualKeyCode);
                bw.Write(ScanCode);
                bw.Write(Flags);
                bw.Write(IsKeyUp);
                return ms.ToArray();
            }
        }

        public static KeyboardPacket Deserialize(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                var packet = new KeyboardPacket();
                packet.VirtualKeyCode = br.ReadUInt16();
                packet.ScanCode = br.ReadUInt16();
                packet.Flags = br.ReadUInt32();
                packet.IsKeyUp = br.ReadBoolean();
                return packet;
            }
        }

        public const int Size = 9;
    }

    public static class ProtocolHelper
    {
        public static void WritePacket(Stream stream, PacketType type, byte[] payload)
        {
            stream.WriteByte((byte)type);
            byte[] lenBytes = BitConverter.GetBytes(payload.Length);
            stream.Write(lenBytes, 0, 4);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        public static bool ReadPacket(Stream stream, out PacketType type, out byte[] payload)
        {
            type = (PacketType)0;
            payload = Array.Empty<byte>();

            int typeVal = stream.ReadByte();
            if (typeVal == -1) return false;

            type = (PacketType)typeVal;

            byte[] lenBytes = new byte[4];
            int lenRead = 0;
            while (lenRead < 4)
            {
                int r = stream.Read(lenBytes, lenRead, 4 - lenRead);
                if (r <= 0) return false;
                lenRead += r;
            }

            int payloadLen = BitConverter.ToInt32(lenBytes, 0);
            if (payloadLen < 0 || payloadLen > 50 * 1024 * 1024) return false; // Max 50MB protection

            payload = new byte[payloadLen];
            int payloadRead = 0;
            while (payloadRead < payloadLen)
            {
                int r = stream.Read(payload, payloadRead, payloadLen - payloadRead);
                if (r <= 0) return false;
                payloadRead += r;
            }

            return true;
        }
    }
}
