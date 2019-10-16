﻿using System;
using System.Collections.Generic;
using System.IO;

namespace PakReader
{
    // TODO: Optimize (it's just a port from UE's code right now)

    public sealed class LocResFile
    {
        static readonly FGuid LocResMagic = new FGuid { A = 0x7574140E, B = 0xFC034A67, C = 0x9D90154A, D = 0x1B7F37C3 };

        public readonly Dictionary<string, Dictionary<string, string>> Entries = new Dictionary<string, Dictionary<string, string>>();

        public LocResFile(string path) : this(File.OpenRead(path)) { }

        public LocResFile(Stream stream) : this(new BinaryReader(stream)) { }

        public LocResFile(BinaryReader reader)
        {
            long StartPos = reader.BaseStream.Position;
            var VersionNumber = LocResVersion.LEGACY;
            {
                var guid = new FGuid(reader);
                if (guid == LocResMagic)
                {
                    VersionNumber = (LocResVersion)reader.ReadByte();
                }
                else
                {
                    // Legacy LocRes files lack the magic number, assume that's what we're dealing with, and seek back to the start of the file
                    reader.BaseStream.Position = StartPos;
                }
            }

            if (VersionNumber > LocResVersion.LATEST)
            {
                throw new IOException($"LocRes file is too new to be loaded! (File Version: {(byte)VersionNumber}, Loader Version: {(byte)LocMetaVersion.LATEST})");
            }

            // Read the localized string array
            var LocalizedStringArray = new FTextLocalizationResourceString[0];
            if (VersionNumber >= LocResVersion.COMPACT)
            {
                long LocalizedStringArrayOffset = reader.ReadInt64();
                if (LocalizedStringArrayOffset > int.MaxValue)
                {
                    Console.WriteLine("bruh "+reader.BaseStream.Position);
                    throw new FileLoadException();
                }
                if (LocalizedStringArrayOffset != -1)
                {
                    if (VersionNumber >= LocResVersion.OPTIMIZED)
                    {
                        long CurrentFileOffset = reader.BaseStream.Position;
                        reader.BaseStream.Position = StartPos + LocalizedStringArrayOffset;
                        LocalizedStringArray = reader.ReadTArray(() => new FTextLocalizationResourceString(reader));
                        reader.BaseStream.Position = CurrentFileOffset;
                    }
                    else
                    {
                        string[] TmpLocalizedStringArray;

                        long CurrentFileOffset = reader.BaseStream.Position;
                        reader.BaseStream.Position = StartPos + LocalizedStringArrayOffset;
                        TmpLocalizedStringArray = reader.ReadTArray(() => CleanString(reader.ReadFString()));
                        reader.BaseStream.Position = CurrentFileOffset;

                        LocalizedStringArray = new FTextLocalizationResourceString[TmpLocalizedStringArray.Length];
                        for (int i = 0; i < TmpLocalizedStringArray.Length; i++)
                        {
                            LocalizedStringArray[i] = new FTextLocalizationResourceString { String = TmpLocalizedStringArray[i], RefCount = -1 };
                        }
                    }
                }
            }

            // Read entries count
            if (VersionNumber >= LocResVersion.OPTIMIZED)
            {
                uint EntriesCount = reader.ReadUInt32();
                // No need for initializer
                // Link: https://github.com/EpicGames/UnrealEngine/blob/7d9919ac7bfd80b7483012eab342cb427d60e8c9/Engine/Source/Runtime/Core/Private/Internationalization/TextLocalizationResource.cpp#L266
            }

            // Read namespace count
            uint NamespaceCount = reader.ReadUInt32();

            for (uint i = 0; i < NamespaceCount; i++)
            {
                // Read namespace
                var Namespace = VersionNumber >= LocResVersion.OPTIMIZED ? new FTextKey(reader) : new FTextKey(CleanString(reader.ReadFString()));
                var Entries = new Dictionary<string, string>();

                // Read key count
                uint KeyCount = reader.ReadUInt32();

                for (uint j = 0; j < KeyCount; j++)
                {
                    // Read key
                    var Key = VersionNumber >= LocResVersion.OPTIMIZED ? new FTextKey(reader) : new FTextKey(CleanString(reader.ReadFString()));
                    FEntry NewEntry;
                    NewEntry.SourceStringHash = reader.ReadUInt32();

                    if (VersionNumber >= LocResVersion.COMPACT)
                    {
                        int LocalizedStringIndex = reader.ReadInt32();

                        if (LocalizedStringArray.Length > LocalizedStringIndex)
                        {
                            // Steal the string if possible
                            var LocalizedString = LocalizedStringArray[LocalizedStringIndex];
                            if (LocalizedString.RefCount == 1)
                            {
                                NewEntry.LocalizedString = LocalizedString.String;
                                LocalizedString.RefCount--;
                            }
                            else
                            {
                                NewEntry.LocalizedString = LocalizedString.String;
                                if (LocalizedString.RefCount != -1)
                                {
                                    LocalizedString.RefCount--;
                                }
                            }
                        }
                        else
                        {
                            throw new IOException($"LocRes has an invalid localized string index for namespace '{Namespace}' and key '{Key}'. This entry will have no translation.");
                        }
                    }
                    else
                    {
                        NewEntry.LocalizedString = CleanString(reader.ReadFString());
                    }
                    Entries.Add(Key.String, NewEntry.LocalizedString);
                }
                this.Entries.Add(Namespace.String, Entries);
            }
        }

        internal static string CleanString(string inp) => string.IsNullOrEmpty(inp) ?
            inp :
            inp[inp.Length - 1] == '\u0000' ?
                inp.Substring(0, inp.Length - 1) :
                inp;
    }

    public sealed class LocMetaFile
    {
        // Fortnite's LocMeta magic constant
        static readonly FGuid LocMetaMagic = new FGuid { A = 0xA14CEE4F, B = 0x83554868, C = 0xBD464C6C, D = 0x7C50DA70}; // new FGuid { A = 0xA14CEE4F, B = 0x83554868, C = 0xBD464C6C, D = 0x7C50DA70 }; (from their github)

        public readonly string NativeCulture;
        public readonly string NativeLocRes;

        public LocMetaFile(string path) : this(File.OpenRead(path)) { }

        public LocMetaFile(Stream stream) : this(new BinaryReader(stream)) { }

        public LocMetaFile(BinaryReader reader)
        {
            if (LocMetaMagic != new FGuid(reader))
            {
                throw new IOException("LocMeta file has an invalid magic constant!");
            }

            var VersionNumber = (LocMetaVersion)reader.ReadByte();
            if (VersionNumber > LocMetaVersion.LATEST)
            {
                throw new IOException($"LocMeta file is too new to be loaded! (File Version: {(byte)VersionNumber}, Loader Version: {(byte)LocMetaVersion.LATEST})");
            }

            NativeCulture = new FString(reader).str;
            NativeLocRes = new FString(reader).str;
        }
    }

    struct FEntry
    {
        public string LocalizedString;
        public uint SourceStringHash;
    }

    struct FTextKey
    {
        public uint StrHash;

        public string String;

        public FTextKey(BinaryReader reader)
        {
            StrHash = reader.ReadUInt32();
            String = LocResFile.CleanString(reader.ReadFString());
        }

        public FTextKey(string str)
        {
            String = str;
            StrHash = 0;
        }
    }

    class FTextLocalizationResourceString
    {
        public string String;
        public int RefCount;

        public FTextLocalizationResourceString(BinaryReader reader)
        {
            String = LocResFile.CleanString(reader.ReadFString());
            RefCount = reader.ReadInt32();
        }

        public FTextLocalizationResourceString() { }
    }

    enum LocMetaVersion : byte
    {
        INITIAL = 0,

        LATEST_PLUS_ONE,
        LATEST = LATEST_PLUS_ONE - 1
    }

    enum LocResVersion : byte
    {
        /** Legacy format file - will be missing the magic number. */
        LEGACY = 0,
        /** Compact format file - strings are stored in a LUT to avoid duplication. */
        COMPACT,
        /** Optimized format file - namespaces/keys are pre-hashed, we know the number of elements up-front, and the number of references for each string in the LUT (to allow stealing). */
        OPTIMIZED,

        LATEST_PLUS_ONE,
        LATEST = LATEST_PLUS_ONE - 1
    }
}
