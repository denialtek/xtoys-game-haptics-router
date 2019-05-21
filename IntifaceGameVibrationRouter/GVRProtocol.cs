﻿using System;
using System.IO;
using System.Xml.Serialization;

namespace IntifaceGameVibrationRouter
{
    [Serializable]
    public class GVRProtocolMessageContainer
    {
        
        public void SendSerialized(Stream aStream)
        {
            // We can't use BinaryFormatter, because we're merging DLLs for the mods and the assemblies won't match. Use XML instead.
            var _formatter = new XmlSerializer(typeof(GVRProtocolMessageContainer));
            // TODO Should figure out a better way to do this, otherwise we're going to create a ton of formatters and clog the GC.
            _formatter.Serialize(aStream, this);
        }

        public static GVRProtocolMessageContainer Deserialize(Stream aStream)
        {
            var _formatter = new XmlSerializer(typeof(GVRProtocolMessageContainer));
            var obj = _formatter.Deserialize(aStream);
            return (GVRProtocolMessageContainer) obj;
        }

        // Basically copying how protobuf deals with aggregate messages. Only one of these should be valid at any time.
        public Log Log;
        public Ping Ping;
        public XInputHaptics XInputHaptics;
        public UnityXRViveHaptics UnityXRViveHaptics;
        public UnityXROculusClipHaptics UnityXROculusClipHaptics;
        public UnityXROculusInputHaptics UnityXROculusInputHaptics;
    }

    [Serializable]
    public class Ping
    {
    }

    [Serializable]
    public class Log
    {
        public string Message;

        public Log()
        {

        }

        public Log(string aMsg)
        {
            Message = aMsg;
        }
    }

    public enum HandSpec
    {
        LEFT,
        RIGHT
    }

    [Serializable]
    public class XInputHaptics
    {
        public uint LeftMotor;
        public uint RightMotor;
    }

    [Serializable]
    public class UnityXRViveHaptics
    {
        public HandSpec Hand;
        public uint Duration;

        public UnityXRViveHaptics()
        {

        }

        public UnityXRViveHaptics(HandSpec aHand, uint aDuration)
        {
            Hand = aHand;
            Duration = aDuration;
        }
    }

    [Serializable]
    public class UnityXROculusInputHaptics
    {
        public HandSpec Hand;
        public float Frequency;
        public float Amplitude;

        public UnityXROculusInputHaptics()
        {

        }

        public UnityXROculusInputHaptics(HandSpec aHand, float aFrequency, float aAmplitude)
        {
            Hand = aHand;
            Frequency = aFrequency;
            Amplitude = aAmplitude;
        }
    }

    [Serializable]
    public class UnityXROculusClipHaptics
    {

    }
}
