﻿// FFXIVAPP.Client
// ChatEntry.cs
// 
// © 2013 Ryan Wilson

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using FFXIVAPP.Client.Helpers;
using FFXIVAPP.Common.Core.Memory;
using FFXIVAPP.Common.Helpers;
using SmartAssembly.Attributes;

namespace FFXIVAPP.Client.Memory
{
    [DoNotObfuscate]
    public static class ChatEntry
    {
        public static ChatLogEntry Process(byte[] raw)
        {
            var chatLogEntry = new ChatLogEntry();
            try
            {
                chatLogEntry.Bytes = raw;
                chatLogEntry.Raw = Encoding.UTF8.GetString(raw.ToArray());
                var cut = (chatLogEntry.Raw.Substring(13, 1) == ":") ? 14 : 13;
                var cleaned = new ChatCleaner(raw).Result;
                chatLogEntry.Line = XmlHelper.SanitizeXmlString(cleaned.Substring(cut));
                chatLogEntry.Line = new ChatCleaner(chatLogEntry.Line).Result;
                chatLogEntry.JP = Encoding.UTF8.GetBytes(chatLogEntry.Line)
                                          .Any(b => b > 128);
                chatLogEntry.Code = chatLogEntry.Raw.Substring(8, 4);
                chatLogEntry.Combined = String.Format("{0}:{1}", chatLogEntry.Code, chatLogEntry.Line);
                chatLogEntry.TimeStamp = DateTimeHelper.UnixTimeStampToDateTime(Int32.Parse(chatLogEntry.Raw.Substring(0, 8), NumberStyles.HexNumber));
            }
            catch (Exception ex)
            {
                chatLogEntry.Bytes = new byte[0];
                chatLogEntry.Raw = "";
                chatLogEntry.Line = "";
                chatLogEntry.Code = "";
                chatLogEntry.Combined = "";
            }
            return chatLogEntry;
        }
    }
}
