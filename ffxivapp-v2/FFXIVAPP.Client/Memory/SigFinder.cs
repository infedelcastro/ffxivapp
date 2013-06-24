﻿// FFXIVAPP.Client
// SigFinder.cs
//  
// Created by Ryan Wilson.
// Copyright © 2007-2013 Ryan Wilson - All Rights Reserved

#region Usings

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVAPP.Common.Utilities;
using NLog;

#endregion

namespace FFXIVAPP.Client.Memory
{
    internal class SigFinder : INotifyPropertyChanged
    {
        #region Property Bindings

        private Dictionary<string, uint> _locations;

        public Dictionary<string, uint> Locations
        {
            get { return _locations ?? (_locations = new Dictionary<string, uint>()); }
            private set
            {
                _locations = value;
                RaisePropertyChanged();
            }
        }

        #endregion

        #region Constants

        private const int WildCardChar = 63;
        private const int MemCommit = 0x1000;
        private const int PageNoAccess = 0x01;
        private const int PageReadwrite = 0x04;
        private const int PageWritecopy = 0x08;
        private const int PageExecuteReadwrite = 0x40;
        private const int PageExecuteWritecopy = 0x80;
        private const int PageGuard = 0x100;
        private const int Writable = PageReadwrite | PageWritecopy | PageExecuteReadwrite | PageExecuteWritecopy | PageGuard;

        #endregion

        #region ResultTypes

        /// <summary>
        ///     Where to return the pointer from
        /// </summary>
        public enum ScanResultType
        {
            /// <summary>
            ///     Read in the pointer before the signature
            /// </summary>
            ValueBeforeSig,

            /// <summary>
            ///     Read in the pointer after the signature
            /// </summary>
            ValueAfterSig,

            /// <summary>
            ///     Read the address at the start of where it found the signature
            /// </summary>
            AddressStartOfSig,

            /// <summary>
            ///     Read the value at the start of the wildcard
            /// </summary>
            ValueAtWildCard
        }

        #endregion

        #region Declarations

        private readonly Process _process;
        private byte[] _memDump;
        private List<UnsafeNativeMethods.MemoryBasicInformation> _regions;

        #endregion

        /// <summary>
        /// </summary>
        /// <param name="process"> </param>
        public SigFinder(Process process)
        {
            _process = process;
            Locations = new Dictionary<string, uint>();
        }

        /// <summary>
        /// </summary>
        /// <param name="process"> </param>
        /// <param name="signatures"> </param>
        public SigFinder(Process process, List<Signature> signatures)
        {
            _process = process;
            LoadOffsets(signatures);
        }

        /// <summary>
        /// </summary>
        /// <param name="signatures"> </param>
        private void LoadOffsets(List<Signature> signatures)
        {
            Func<bool> d = delegate
            {
                var sw = new Stopwatch();
                sw.Start();
                if (_process == null)
                {
                    return false;
                }
                LoadRegions();
                Locations = new Dictionary<string, uint>();
                if (signatures.Any())
                {
                    foreach (var signature in signatures)
                    {
                        Locations.Add(signature.Key, (uint) FindSignature(signature.Value, signature.Offset, ScanResultType.AddressStartOfSig));
                    }
                }
                _memDump = null;
                sw.Stop();
                Debug.Print("SigFinder Completion Time: " + sw.Elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture));
                return true;
            };
            d.BeginInvoke(null, null);
        }

        /// <summary>
        /// </summary>
        private void LoadRegions()
        {
            try
            {
                _regions = new List<UnsafeNativeMethods.MemoryBasicInformation>();
                var address = 0;
                while (true)
                {
                    var info = new UnsafeNativeMethods.MemoryBasicInformation();
                    var result = UnsafeNativeMethods.VirtualQueryEx(_process.Handle, (uint) address, out info, (uint) Marshal.SizeOf(info));
                    if (0 == result)
                    {
                        break;
                    }
                    if (0 != (info.State & MemCommit) && 0 != (info.Protect & Writable) && 0 == (info.Protect & PageGuard))
                    {
                        _regions.Add(info);
                    }
                    address = info.BaseAddress + info.RegionSize;
                }
            }
            catch (Exception ex)
            {
                Logging.Log(LogManager.GetCurrentClassLogger(), "", ex);
            }
        }

        /// <summary>
        ///     Searches the loaded process for the given byte signature
        /// </summary>
        /// <param name="signature">The hex pattern to search for</param>
        /// <returns>The pointer found at the matching location</returns>
        private IntPtr FindSignature(string signature, ScanResultType searchType)
        {
            return FindSignature(signature, 0, searchType);
        }

        /// <summary>
        ///     <para>Searches the loaded process for the given byte signature.</para>
        ///     <para>Uses the character ? as a wildcard</para>
        /// </summary>
        /// <param name="signature">The hex pattern to search for</param>
        /// <param name="offset">An offset to add to the pointer VALUE</param>
        /// <param name="searchType">What type os result to return</param>
        /// <returns>The pointer found at the matching location</returns>
        private IntPtr FindSignature(string signature, int offset, ScanResultType searchType)
        {
            if (signature.Length == 0 || signature.Length % 2 != 0)
            {
                throw new Exception("FindSignature(): Invalid signature");
            }
            foreach (var region in _regions)
            {
                var buffer = new byte[region.RegionSize];
                if (!UnsafeNativeMethods.ReadProcessMemory(_process.Handle, (IntPtr) region.BaseAddress, buffer, region.RegionSize, 0))
                {
                    var errorCode = Marshal.GetLastWin32Error();
                    throw new Exception("FindSignature(): Unable to read memory. Error Code [" + errorCode + "]");
                }
                var searchResult = FindSignature(buffer, signature, offset, searchType);
                if (IntPtr.Zero != searchResult)
                {
                    if (ScanResultType.AddressStartOfSig == searchType)
                    {
                        searchResult = new IntPtr(region.BaseAddress + searchResult.ToInt32());
                    }

                    return searchResult;
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        ///     Searches the buffer for the given hex string and returns the pointer matching the first wildcard location, or the pointer following the pattern if not using wildcards.
        ///     Prefix with &lt;&lt; to always return the pointer preceding the match or &gt;&gt; to always return the pointer following (regardless of wildcards)
        /// </summary>
        /// <param name="buffer">The source binary buffer to search within</param>
        /// <param name="signature">A hex string representation of a sequence of bytes to search for</param>
        /// <param name="offset">An offset to add to the found pointer VALUE.</param>
        /// <param name="searchType"></param>
        /// <returns>A pointer at the matching location</returns>
        private static IntPtr FindSignature(byte[] buffer, string signature, int offset, ScanResultType searchType)
        {
            if (signature.Length == 0 || signature.Length % 2 != 0)
            {
                return IntPtr.Zero;
            }
            var pattern = SigToByte(signature, WildCardChar);
            if (pattern != null)
            {
                var pos = 0;
                for (pos = 0; pos < pattern.Length; pos++)
                {
                    if (pattern[pos] == WildCardChar)
                    {
                        break;
                    }
                }
                var idx = -1;
                idx = pos == pattern.Length ? Horspool(buffer, pattern) : BNDM(buffer, pattern, WildCardChar);
                if (idx < 0)
                {
                    return IntPtr.Zero;
                }
                switch (searchType)
                {
                    case ScanResultType.ValueBeforeSig:
                        return (IntPtr) (BitConverter.ToInt32(buffer, idx - 4) + offset);
                    case ScanResultType.ValueAfterSig:
                        return (IntPtr) (BitConverter.ToInt32(buffer, idx + pattern.Length) + offset);
                    case ScanResultType.AddressStartOfSig:
                        return (IntPtr) (idx + offset);
                    case ScanResultType.ValueAtWildCard:
                    default:
                        return (IntPtr) (BitConverter.ToInt32(buffer, idx + pos) + offset);
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>Backward Nondeterministic Dawg Matching search algorithm</summary>
        /// <param name="buffer">The haystack to search within</param>
        /// <param name="pattern">The needle to locate</param>
        /// <param name="wildcard">The byte to treat as a wildcard character. Note that this only matches one char for one char and does not expand.</param>
        /// <returns>The index the pattern was found at, or -1 if not found</returns>
        private static int BNDM(byte[] buffer, byte[] pattern, byte wildcard)
        {
            var end = pattern.Length < 32 ? pattern.Length : 32;
            var b = new int[256];
            var j = 0;
            for (var i = 0; i < end; ++i)
            {
                if (pattern[i] == wildcard)
                {
                    j |= (1 << end - i - 1);
                }
            }
            if (j != 0)
            {
                for (var i = 0; i < b.Length; i++)
                {
                    b[i] = j;
                }
            }
            j = 1;
            for (var i = end - 1; i >= 0; --i, j <<= 1)
            {
                b[pattern[i]] |= j;
            }
            var pos = 0;
            while (pos <= buffer.Length - pattern.Length)
            {
                j = pattern.Length - 1;
                var last = pattern.Length;
                var d = -1;
                while (d != 0)
                {
                    d &= b[buffer[pos + j]];

                    if (d != 0)
                    {
                        if (j == 0)
                        {
                            return pos;
                        }

                        last = j;
                    }
                    --j;
                    d <<= 1;
                }
                pos += last;
            }
            return -1;
        }

        /// <summary>Boyer-Moore-Horspool search algorithm</summary>
        /// <param name="buffer">The haystack to search within</param>
        /// <param name="pattern">The needle to locate</param>
        /// <returns>The index the pattern was found at, or -1 if not found</returns>
        private static int Horspool(byte[] buffer, byte[] pattern)
        {
            var bcs = new int[256];
            var scan = 0;
            for (scan = 0; scan < 256; scan = scan + 1)
            {
                bcs[scan] = pattern.Length;
            }
            var last = pattern.Length - 1;
            for (scan = 0; scan < last; scan = scan + 1)
            {
                bcs[pattern[scan]] = last - scan;
            }
            var hidx = 0;
            var hlen = buffer.Length;
            var nlen = pattern.Length;
            while (hidx <= hlen - nlen)
            {
                for (scan = last; buffer[hidx + scan] == pattern[scan]; scan = scan - 1)
                {
                    if (scan == 0)
                    {
                        return hidx;
                    }
                }
                hidx += bcs[buffer[hidx + last]];
            }
            return -1;
        }

        /// <summary>
        ///     Convert a hex string to a binary array while preserving any wildcard characters.
        /// </summary>
        /// <param name="signature">A hex string "signature"</param>
        /// <param name="wildcard">The byte to treat as the wildcard</param>
        /// <returns>The converted binary array. Null if the conversion failed.</returns>
        private static byte[] SigToByte(string signature, byte wildcard)
        {
            var pattern = new byte[signature.Length / 2];
            var hexTable = new[]
            {
                0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F
            };
            try
            {
                for (int x = 0, i = 0; i < signature.Length; i += 2, x += 1)
                {
                    if (signature[i] == wildcard)
                    {
                        pattern[x] = wildcard;
                    }
                    else
                    {
                        pattern[x] = (byte) (hexTable[Char.ToUpper(signature[i]) - '0'] << 4 | hexTable[Char.ToUpper(signature[i + 1]) - '0']);
                    }
                }
                return pattern;
            }
            catch
            {
                return null;
            }
        }

        #region Implementation of INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        private void RaisePropertyChanged([CallerMemberName] string caller = "")
        {
            PropertyChanged(this, new PropertyChangedEventArgs(caller));
        }

        #endregion
    }
}
