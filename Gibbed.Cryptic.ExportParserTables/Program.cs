﻿/* Copyright (c) 2012 Rick (rick 'at' gibbed 'dot' us)
 * 
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 * 
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 * 
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 * 
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 * 
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Gibbed.IO;
using Parser = Gibbed.Cryptic.FileFormats.Parser;

namespace Gibbed.Cryptic.ExportParserTables
{
    internal class Program
    {
        private static string GetEnumType(
            ProcessMemory memory,
            uint baseAddress)
        {
            switch (memory.ReadU32(baseAddress))
            {
                case 1: return "int";
                case 2: return "string";
                default: throw new NotSupportedException();
            }
        }

        private static List<KeyValuePair<string, string>> ReadKeyValueList(
            ProcessMemory memory,
            uint baseAddress)
        {
            var entries = new List<KeyValuePair<string, string>>();
            var listAddress = memory.ReadU32(baseAddress);

            if (listAddress != 0)
            {
                var count = memory.ReadS32(listAddress + 8);
                if (count > 0)
                {
                    var entriesAddress = memory.ReadU32(listAddress + 4);
                    var data = memory.ReadBytes(entriesAddress, count * 8);

                    for (int i = 0; i < count; i++)
                    {
                        var name = memory.ReadStringZ(BitConverter.ToUInt32(data, (i * 8) + 0));
                        var value = memory.ReadStringZ(BitConverter.ToUInt32(data, (i * 8) + 4));
                        entries.Add(new KeyValuePair<string, string>(name, value));
                    }
                }
            }

            return entries;
        }

        private static List<KeyValuePair<string, string>> ReadEnum(
            ProcessMemory memory,
            uint baseAddress)
        {
            var elements = new List<KeyValuePair<string, string>>();

            var valueType = 4;

            while (true)
            {
                var type = memory.ReadU32(baseAddress);
                if (type == 0)
                {
                    break;
                }

                switch (type)
                {
                    case 1:
                    {
                        valueType = 1;
                        baseAddress += 8;
                        break;
                    }

                    case 2:
                    {
                        valueType = 2;
                        baseAddress += 8;
                        break;
                    }

                    // dynamic bullshit
                    case 3:
                    {
                        baseAddress += 8;
                        break;
                    }

                    case 5:
                    {
                        var parent = memory.ReadU32(baseAddress + 4);
                        var nested = ReadEnum(memory, parent);
                        foreach (var kv in nested)
                        {
                            elements.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
                        }
                        return elements;
                    }

                    default:
                    {
                        var name = memory.ReadStringZ(type);

                        object value;
                        switch (valueType)
                        {
                            case 1:
                            {
                                value = memory.ReadS32(baseAddress + 4);
                                baseAddress += 8;
                                break;
                            }

                            case 2:
                            {
                                value = memory.ReadStringZ(baseAddress + 4);
                                baseAddress += 8;
                                break;
                            }

                            default:
                            {
                                throw new NotImplementedException();
                            }
                        }

                        elements.Add(new KeyValuePair<string, string>(name, value.ToString()));
                        break;
                    }
                }
            }

            return elements;
        }

        private static void ExportEnum(
            ProcessMemory memory,
            uint address,
            List<KeyValuePair<string, string>> elements,
            XmlWriter xml)
        {
            xml.WriteStartElement("elements");
            xml.WriteAttributeString("type", GetEnumType(memory, address));

            foreach (var kv in elements)
            {
                xml.WriteStartElement("element");
                xml.WriteAttributeString("name", kv.Key);
                xml.WriteValue(kv.Value);
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        private static string GetTokenName(NativeColumn column, out Parser.ColumnFlags flags)
        {
            var check = Parser.ColumnFlags.None;
            check |= column.Flags & Parser.ColumnFlags.FIXED_ARRAY; // 0x80000
            check |= column.Flags & Parser.ColumnFlags.EARRAY; // 0x40000
            check |= column.Flags & Parser.ColumnFlags.INDIRECT; // 0x100000

            var token = Parser.GlobalTokens.GetToken(column.Token);

            string name = null;

            if ((check & Parser.ColumnFlags.INDIRECT) == 0)
            {
                switch (check)
                {
                    case Parser.ColumnFlags.FIXED_ARRAY: name = token.NameDirectFixedArray; break;
                    case Parser.ColumnFlags.EARRAY: name = token.NameDirectArray; break;
                    //default: name = token.NameDirectValue; break;
                }
            }
            else
            {
                switch (check & ~Parser.ColumnFlags.INDIRECT)
                {
                    case Parser.ColumnFlags.FIXED_ARRAY: name = token.NameIndirectFixedArray; break;
                    case Parser.ColumnFlags.EARRAY: name = token.NameIndirectArray; break;
                    default: name = token.NameIndirectValue; break;
                }
            }

            if (name != null)
            {
                flags = column.Flags;
                flags &= ~Parser.ColumnFlags.FIXED_ARRAY;
                flags &= ~Parser.ColumnFlags.EARRAY;
                flags &= ~Parser.ColumnFlags.INDIRECT;
                return name;
            }

            flags = column.Flags;
            return token.NameDirectValue;
        }

        private static Dictionary<Parser.ColumnFlags, string> GenerateColumnFlagNames()
        {
            var names = new Dictionary<Parser.ColumnFlags, string>();
            foreach (var name in Enum.GetNames(typeof(Parser.ColumnFlags)))
            {
                names.Add((Parser.ColumnFlags)Enum.Parse(typeof(Parser.ColumnFlags), name), name);
            }
            return names;
        }

        private static Dictionary<Parser.ColumnFlags, string> ColumnFlagNames = GenerateColumnFlagNames();

        /*
        private static Dictionary<Parser.ColumnFlags, string> BasicFlags = new Dictionary<Parser.ColumnFlags,string>()
        {
            {Parser.ColumnFlags.FIXED_ARRAY, "FIXED_ARRAY"},
            {Parser.ColumnFlags.INDIRECT, "INDIRECT"},
            {Parser.ColumnFlags.POOL_STRING, "POOL_STRING"},
            {Parser.ColumnFlags.ESTRING, "ESTRING"},
            {Parser.ColumnFlags.OBJECTTYPE, "OBJECTTYPE"},
            {Parser.ColumnFlags.STRUCTPARAM, "STRUCTPARAM"},
            {Parser.ColumnFlags.ALWAYS_ALLOC, "ALWAYS_ALLOC"},
            {Parser.ColumnFlags.NON_NULL_REF, "NON_NULL_REF"},
            {Parser.ColumnFlags.REQUIRED, "REQUIRED"},
            {Parser.ColumnFlags.NO_WRITE, "NO_WRITE"},
            {Parser.ColumnFlags.NO_NETSEND, "NO_NETSEND"},
            {Parser.ColumnFlags.FLATEMBED, "FLATEMBED"},
            {Parser.ColumnFlags.NO_TEXT_SAVE, "NO_TEXT_SAVE"},
            {Parser.ColumnFlags.GLOBAL_NAME, "GLOBAL_NAME"},
            {Parser.ColumnFlags.USEDFIELD, "USEDFIELD"},
            {Parser.ColumnFlags.USEROPTIONBIT_1, "USEROPTIONBIT_1"},
            {Parser.ColumnFlags.USEROPTIONBIT_2, "USEROPTIONBIT_2"},
            {Parser.ColumnFlags.USEROPTIONBIT_3, "USEROPTIONBIT_3"},
            {Parser.ColumnFlags.POOL_STRING_DB, "POOL_STRING_DB"},
            {Parser.ColumnFlags.DEFAULT_FIELD, "DEFAULT_FIELD"},
            {Parser.ColumnFlags.DEMO_IGNORE, "DEMO_IGNORE"},
            {Parser.ColumnFlags.PUPPET_NO_COPY, "PUPPET_NO_COPY"},
            {Parser.ColumnFlags.SUBSCRIBE, "SUBSCRIBE"},
            {Parser.ColumnFlags.SERVER_ONLY, "SERVER_ONLY"},
            {Parser.ColumnFlags.CLIENT_ONLY, "CLIENT_ONLY"},
            {Parser.ColumnFlags.SELF_ONLY, "SELF_ONLY"},
            {Parser.ColumnFlags.SELF_AND_TEAM_ONLY, "SELF_AND_TEAM_ONLY"},
            {Parser.ColumnFlags.LOGIN_SUBSCRIBE, "LOGIN_SUBSCRIBE"},
            {Parser.ColumnFlags.KEY, "KEY"},
            {Parser.ColumnFlags.PERSIST, "PERSIST"},
            {Parser.ColumnFlags.NO_TRANSACT, "NO_TRANSACT"},
            {Parser.ColumnFlags.SOMETIMES_TRANSACT, "SOMETIMES_TRANSACT"},
            {Parser.ColumnFlags.VITAL_REF, "VITAL_REF"},
            {Parser.ColumnFlags.NON_NULL_REF__ERROR_ONLY, "NON_NULL_REF__ERROR_ONLY"},
            {Parser.ColumnFlags.DIRTY_BIT, "DIRTY_BIT"},
            {Parser.ColumnFlags.NO_INHERIT, "NO_INHERIT"},
            {Parser.ColumnFlags.IGNORE_STRUCT, "IGNORE_STRUCT"},
            {Parser.ColumnFlags.SPECIAL_DEFAULT, "SPECIAL_DEFAULT"},
            {Parser.ColumnFlags.PARSETABLE_INFO, "PARSETABLE_INFO"},
            {Parser.ColumnFlags.INHERITANCE_STRUCT, "INHERITANCE_STRUCT"},
            {Parser.ColumnFlags.STRUCT_NORECURSE, "STRUCT_NORECURSE"},
            {Parser.ColumnFlags.CASE_SENSITIVE, "CASE_SENSITIVE"},
            {Parser.ColumnFlags.EDIT_ONLY, "EDIT_ONLY"},
            {Parser.ColumnFlags.NO_INDEX, "NO_INDEX"},
            {Parser.ColumnFlags.NO_LOG, "NO_LOG"},
        };
        */

        private static string[] FloatRounding =
        {
            null,
            "HUNDREDTHS",
            "TENTHS",
            "ONES",
            "FIVES",
            "TENS",
        };

        public static Parser.ColumnFlags ColumnFlagsMask = ColumnFlagNames
            .Aggregate<KeyValuePair<Parser.ColumnFlags, string>, Parser.ColumnFlags>(
                Parser.ColumnFlags.None,
                (a, b) => a | b.Key);

        private static uint HashKeyValueList(
            ProcessMemory memory,
            uint baseAddress,
            uint hash)
        {
            var entries = new List<KeyValuePair<string, string>>();
            var listAddress = memory.ReadU32(baseAddress);

            var count = memory.ReadS32(listAddress + 8);
            if (count > 0)
            {
                var entriesAddress = memory.ReadU32(listAddress + 4);
                var data = memory.ReadBytes(entriesAddress, count * 8);

                for (int i = 0; i < count; i++)
                {
                    var name = memory.ReadStringZ(BitConverter.ToUInt32(data, (i * 8) + 0));
                    var value = memory.ReadStringZ(BitConverter.ToUInt32(data, (i * 8) + 4));
                    entries.Add(new KeyValuePair<string, string>(name, value));
                }
            }

            var sb = new StringBuilder();
            foreach (var entry in entries.OrderBy(e => e.Key.ToLowerInvariant()))
            {
                sb.Append(entry.Key);
                sb.Append(entry.Value);
            }

            return Adler32.Hash(sb.ToString(), hash);
        }

        private static uint HashStaticDefineList(
            ProcessMemory memory,
            uint baseAddress,
            uint hash)
        {
            var valueType = 4;

            while (true)
            {
                var type = memory.ReadU32(baseAddress);
                if (type == 0)
                {
                    break;
                }

                switch (type)
                {
                    case 1:
                    {
                        valueType = 1;
                        baseAddress += 8;
                        break;
                    }

                    case 2:
                    {
                        valueType = 2;
                        baseAddress += 8;
                        break;
                    }

                    case 3:
                    {
                        var listAddress = memory.ReadU32(baseAddress + 4);
                        baseAddress += 8;

                        if (listAddress != 0)
                        {
                            hash = HashKeyValueList(memory, listAddress, hash);
                        }

                        break;
                    }

                    case 5:
                    {
                        var parent = memory.ReadU32(baseAddress + 4);
                        return HashStaticDefineList(memory, parent, hash);
                    }

                    default:
                    {
                        var name = memory.ReadStringZ(type);
                        hash = Adler32.Hash(name, hash);

                        switch (valueType)
                        {
                            case 1:
                            {
                                var value = memory.ReadU32(baseAddress + 4);
                                hash = Adler32.Hash(value, hash);
                                baseAddress += 8;
                                break;
                            }

                            case 2:
                            {
                                var value = memory.ReadStringZ(baseAddress + 4);
                                hash = Adler32.Hash(value, hash);
                                baseAddress += 8;
                                break;
                            }

                            default:
                            {
                                throw new NotImplementedException();
                            }
                        }

                        break;
                    }
                }
            }

            return hash;
        }

        private static uint HashTable(
            ProcessMemory memory,
            uint address,
            uint hash)
        {
            var columns = new List<KeyValuePair<NativeColumn, string>>();
            var currentAddress = address;
            while (true)
            {
                var column = memory.ReadStructure<NativeColumn>(currentAddress);
                currentAddress += 40;
                
                var name = memory.ReadStringZ(column.NamePointer);
                
                if (column.Type == 0)
                {
                    if (string.IsNullOrEmpty(name) == true)
                    {
                        break;
                    }
                }

                columns.Add(new KeyValuePair<NativeColumn, string>(column, name));
            }

            foreach (var columnKV in columns)
            {
                var column = columnKV.Key;

                if ((column.Flags & Parser.ColumnFlags.ALIAS) != 0)
                {
                    continue;
                }
                else if ((column.Flags & Parser.ColumnFlags.UNKNOWN_32) != 0)
                {
                    continue;
                }

                var name = columnKV.Value;

                if (string.IsNullOrEmpty(name) == false)
                {
                    hash = Adler32.Hash(name, hash);
                }

                hash = Adler32.Hash(column.Type, hash);

                var token = Parser.GlobalTokens.GetToken(column.Token);

                switch (token.GetParameter(column.Flags, 0))
                {
                    case Parser.ColumnParameter.NumberOfElements:
                    case Parser.ColumnParameter.Default:
                    case Parser.ColumnParameter.StringLength:
                    case Parser.ColumnParameter.Size:
                    {
                        hash = Adler32.Hash(column.Parameter0, hash);
                        break;
                    }

                    case Parser.ColumnParameter.BitOffset:
                    {
                        hash = Adler32.Hash(column.Parameter0 >> 16, hash);
                        break;
                    }

                    case Parser.ColumnParameter.DefaultString:
                    case Parser.ColumnParameter.CommandString:
                    {
                        if (column.Parameter0 != 0)
                        {
                            hash = Adler32.Hash(memory.ReadStringZ(column.Parameter0), hash);
                        }
                        break;
                    }
                }

                var param1 = token.GetParameter(column.Flags, 1);

                if (column.Parameter1 != 0 &&
                    (column.Token == 20 || column.Token == 21) &&
                    address != column.Parameter1 &&
                    (column.Flags & Parser.ColumnFlags.STRUCT_NORECURSE) == 0)
                {
                    hash = HashTable(memory, column.Parameter1, hash);
                }

                if (column.Parameter1 != 0 &&
                    param1 == Parser.ColumnParameter.StaticDefineList)
                {
                    hash = HashStaticDefineList(memory, column.Parameter1, hash);
                }

                if (column.Token == 23)
                {
                    var unknown20 = memory.ReadStringZ(column.Unknown20);
                    if (string.IsNullOrEmpty(unknown20) == false)
                    {
                        hash = Adler32.Hash(unknown20, hash);
                    }
                }
            }

            return hash;
        }

        private static string[] FormatNames =
        {
            null,
            "IP", // 1
            "UNSIGNED", // 2
            "DATESS2000", // 3
            "PERCENT", // 4
            "NOPATH", // 5
            "HSV", // 6
            null,
            "TEXTURE", // 8
            "COLOR", // 9
            "FRIENDLYDATE", // 10
            "FRIENDLYSS2000", // 11
            "KBYTES", // 12
            "FLAGS", // 13
        };

        private static void ExportParserTable(
            ProcessMemory memory,
            uint address,
            XmlWriter xml,
            List<KeyValuePair<string, uint>> parsers,
            List<KeyValuePair<string, uint>> enums)
        {
            /*xml.WriteComment(string.Format(" {0:X8} ",
                0x00400000u + (address - (uint)memory.MainModuleAddress.ToInt32())));*/

            var currentAddress = address;

            var columns = new List<KeyValuePair<NativeColumn, string>>();
            while (true)
            {
                var column = memory.ReadStructure<NativeColumn>(currentAddress);
                currentAddress += 40;
                
                var name = memory.ReadStringZ(column.NamePointer);
                
                if (column.Type == 0)
                {
                    if (string.IsNullOrEmpty(name) == true)
                    {
                        break;
                    }
                }

                columns.Add(new KeyValuePair<NativeColumn, string>(column, name));
            }

            foreach (var columnKV in columns)
            {
                var column = columnKV.Key;
                var name = columnKV.Value;

                xml.WriteStartElement("column");

                if (string.IsNullOrEmpty(name) == false)
                {
                    xml.WriteAttributeString("name", name);
                }

                var token = Parser.GlobalTokens.GetToken(column.Token);

                Parser.ColumnFlags flags;
                var tokenName = GetTokenName(column, out flags);

                xml.WriteAttributeString("type", tokenName);

                if (column.Token != 0 &&
                    column.Token != 1 &&
                    column.Token != 2)
                {
                    xml.WriteElementString("offset", column.Offset.ToString());
                }

                var values = new List<string>();

                if ((column.Flags & Parser.ColumnFlags.PARSETABLE_INFO) != 0)
                {
                    xml.WriteStartElement("flags");
                    xml.WriteElementString("flag", "PARSETABLE_INFO");
                    xml.WriteEndElement();

                    var formatString = memory.ReadStringZ(column.Unknown20);
                    if (string.IsNullOrEmpty(formatString) == false)
                    {
                        xml.WriteElementString("format_string", formatString);
                    }
                }
                else
                {
                    if ((flags & ColumnFlagsMask) != 0)
                    {
                        xml.WriteStartElement("flags");

                        foreach (var kv in ColumnFlagNames)
                        {
                            if ((flags & kv.Key) != 0)
                            {
                                xml.WriteElementString("flag", kv.Value);
                            }
                        }

                        xml.WriteEndElement();
                    }

                    if ((column.Flags & Parser.ColumnFlags.ALIAS) != 0)
                    {
                        var aliased = columns
                            .Where(c => c.Key != column)
                            .Where(c => (c.Key.Flags & Parser.ColumnFlags.ALIAS) == 0)
                            .Where(c => c.Key.Offset == column.Offset)
                            .Where(c => c.Key.Token == column.Token)
                            .Where(c => c.Key.Token != 23 || c.Key.Parameter0 == column.Parameter0)
                            .FirstOrDefault();

                        if (aliased.Key != null)
                        {
                            xml.WriteElementString("redundant_name", aliased.Value);
                        }
                    }
                    //else
                    {
                        if (column.MinBits != 0)
                        {
                            if (column.Token == 7)
                            {
                                xml.WriteElementString("float_rounding", FloatRounding[column.MinBits]);
                            }
                            else
                            {
                                xml.WriteElementString("min_bits", column.MinBits.ToString());
                            }
                        }

                        switch (token.GetParameter(column.Flags, 0))
                        {
                            case Parser.ColumnParameter.NumberOfElements:
                            {
                                xml.WriteElementString("num_elements", ((int)column.Parameter0).ToString());
                                break;
                            }

                            case Parser.ColumnParameter.Default:
                            {
                                if (column.Parameter0 != 0)
                                {
                                    xml.WriteElementString("default", ((int)column.Parameter0).ToString());
                                }
                                break;
                            }

                            case Parser.ColumnParameter.StringLength:
                            {
                                xml.WriteElementString("string_length", ((int)column.Parameter0).ToString());
                                break;
                            }

                            case Parser.ColumnParameter.DefaultString:
                            {
                                if (column.Parameter0 != 0)
                                {
                                    xml.WriteElementString("default_string", memory.ReadStringZ(column.Parameter0) ?? "");
                                }
                                break;
                            }

                            case Parser.ColumnParameter.CommandString:
                            {
                                if (column.Parameter0 != 0)
                                {
                                    xml.WriteElementString("command_string", memory.ReadStringZ(column.Parameter0) ?? "");
                                }
                                break;
                            }

                            case Parser.ColumnParameter.Size:
                            {
                                xml.WriteElementString("size", ((int)column.Parameter0).ToString());
                                break;
                            }

                            case Parser.ColumnParameter.BitOffset:
                            {
                                xml.WriteElementString("bit_offset", ((int)column.Parameter0).ToString());
                                break;
                            }
                        }

                        switch (token.GetParameter(column.Flags, 1))
                        {
                            case Parser.ColumnParameter.StaticDefineList:
                            {
                                if (column.Parameter1 != 0)
                                {
                                    xml.WriteStartElement("static_define_list");

                                    var kv = enums.SingleOrDefault(e => e.Value == column.Parameter1);
                                    if (kv.Key == null)
                                    {
                                        xml.WriteComment(" dynamic enum? ");
                                        /*xml.WriteStartElement("enum");
                                        ExportEnum(memory, column.Parameter1, xml);
                                        xml.WriteEndElement();*/
                                    }
                                    else
                                    {
                                        xml.WriteAttributeString("external", kv.Key);
                                    }

                                    //xml.WriteComment(string.Format(" {0:X8} ", column.Parameter1));
                                    xml.WriteEndElement();
                                }
                                break;
                            }

                            case Parser.ColumnParameter.DictionaryName:
                            {
                                if (column.Parameter1 != 0)
                                {
                                    xml.WriteElementString("dictionary_name", memory.ReadStringZ(column.Parameter1) ?? "");
                                }
                                break;
                            }

                            case Parser.ColumnParameter.Subtable:
                            {
                                if (column.Parameter1 == 0)
                                {
                                    xml.WriteElementString("subtable", "NULL?");
                                }
                                else
                                {
                                    xml.WriteStartElement("subtable");

                                    var parser = parsers.SingleOrDefault(p => p.Value == column.Parameter1);
                                    if (parser.Key == null)
                                    {
                                        //xml.WriteElementString("subtable", "NOT FOUND? " + column.Parameter1.ToString("X*"));

                                        xml.WriteStartElement("table");
                                        ExportParserTable(memory, column.Parameter1, xml, parsers, enums);
                                        xml.WriteEndElement();
                                    }
                                    else
                                    {
                                        xml.WriteAttributeString("external", parser.Key);
                                    }

                                    xml.WriteEndElement();
                                }
                                break;
                            }
                        }

                        var format = column.Unknown1C & 0xFF;
                        if (format != 0)
                        {
                            if (format < FormatNames.Length &&
                                FormatNames[format] != null)
                            {
                                xml.WriteElementString("format", FormatNames[format]);
                            }
                            else
                            {
                                xml.WriteElementString("format_raw", format.ToString());
                            }
                        }
                    }
                }

                xml.WriteEndElement();
            }
        }

        private static string TranslateArgumentType(
            NativeExpressionArgument arg,
            ProcessMemory memory)
        {
            switch (arg.Type)
            {
                case 0x0000: return "void";
                case 0x0002: return "int";
                case 0x0004: return "flt";
                case 0x0005: return "intarray";
                case 0x0006: return "floatarray";
                case 0x0007: return "Vec3";
                case 0x0008: return "Vec4";
                case 0x0009: return "Mat4";
                case 0x000A: return "Quat";
                case 0x000B: return "str";
                case 0x000C: return "multivalarray";
                case 0x0080: return "entityarray";
                case 0x0081: return memory.ReadStringZ(arg.ptrTypeNamePointer);
                case 0x0082: return "MultiVal";
                case 0x2809: return "loc";
            }

            return string.Format("*INV:{0:X8}*",
                arg.Type);

            throw new NotSupportedException();
        }

        private static void ExportExpressionFunction(
            string itemName, uint pointer,
            ProcessMemory memory,
            TextWriter writer)
        {
            var sb = new StringBuilder();

            var func = memory.ReadStructure<NativeExpressionFunction>(pointer);

            sb.AppendFormat("[{0:X8}] ", func.handler);

            var funcName = memory.ReadStringZ(func.FuncNamePointer);
            var codeName = memory.ReadStringZ(func.ExprCodeNamePointer);

            var tags = new string[12];
            for (int i = 0; i < 12; i++)
            {
                tags[i] = memory.ReadStringZ(func.tags[i]);
            }

            var validTags = tags
                .Where(t => string.IsNullOrWhiteSpace(t) == false)
                .ToArray();
            if (validTags.Length > 0)
            {
                sb.AppendFormat("{0} : ",
                    string.Join(", ", validTags));
            }

            sb.AppendFormat("{0} {1}(",
                TranslateArgumentType(func.ReturnType, memory),
                funcName);
            
            for (int i = 0; i < func.argc; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.AppendFormat("{0} {1}",
                    TranslateArgumentType(func.args[i], memory),
                    memory.ReadStringZ(func.args[i].NamePointer));
            }

            sb.Append(")");

            writer.WriteLine(sb.ToString());
        }

        public static void Main(string[] args)
        {
            var process = Process.GetProcessesByName("DebugClient")
                .Where(p =>
                    p.MainWindowTitle == "Champions Online" ||
                    p.MainWindowTitle == "Star Trek Online")
                .FirstOrDefault();
            if (process == null)
            {
                return;
            }

            var outputPath = Path.Combine("parsers", process.MainWindowTitle);
            Directory.CreateDirectory(outputPath);

            string[] dontHash =
            {
                /* these are badly defined recursive structures that
                 * make hashing blow up (even the game would too!) */
                "WrlDef",
                "WrlScene",
                "WrlChildren",
                
                // now ignoring XML* wholesale in the check code
                /*"XMLArray",
                "XMLValue",
                "XMLParseState",
                "XMLMember",*/

                "MemoryBudget",
                "ModuleMemOperationStats",

                "TestServerGlobal",
                "TestServerGlobalReference",
                "TestServerGlobalValue",

                /* this one just takes forever :P */
                //"Entity",
            };

            using (var memory = new ProcessMemory())
            {
                if (memory.Open(process) == false)
                {
                    return;
                }

                // expression functions
                var exprFuncs = new List<KeyValuePair<string, uint>>();
                {
                    var stashPointer = Locator.FindExpressionFunctionTable(memory);
                    var stashCount = memory.ReadS32(stashPointer + 0x08);
                    var stashEntryPointer = memory.ReadU32(stashPointer + 0x14);
                    var stashEntries = memory.ReadBytes(stashEntryPointer, stashCount * 8);

                    for (int i = 0; i < stashCount; i++)
                    {
                        var namePointer = BitConverter.ToUInt32(stashEntries, (i * 8) + 0);
                        var dataPointer = BitConverter.ToUInt32(stashEntries, (i * 8) + 4);

                        if (namePointer == 0 &&
                            dataPointer == 0)
                        {
                            continue;
                        }

                        var name = memory.ReadStringZ(namePointer);
                        exprFuncs.Add(new KeyValuePair<string, uint>(name, dataPointer));
                    }
                }

                using (var output = File.Create(Path.Combine(outputPath, "expression functions.txt")))
                {
                    var writer = new StreamWriter(output);
                    foreach (var kv in exprFuncs.OrderBy(kv => kv.Key))
                    {
                        ExportExpressionFunction(kv.Key, kv.Value, memory, writer);
                    }
                    writer.Flush();
                }

                var tempEnums = new List<KeyValuePair<string, uint>>();
                var enums = new List<KeyValuePair<string, uint>>();
                {
                    var stashPointer = Locator.FindEnumTable(memory);
                    var stashCount = memory.ReadS32(stashPointer + 0x08);
                    var stashEntryPointer = memory.ReadU32(stashPointer + 0x14);
                    var stashEntries = memory.ReadBytes(stashEntryPointer, stashCount * 8);

                    for (int i = 0; i < stashCount; i++)
                    {
                        var namePointer = BitConverter.ToUInt32(stashEntries, (i * 8) + 0);
                        var dataPointer = BitConverter.ToUInt32(stashEntries, (i * 8) + 4);

                        if (namePointer == 0 &&
                            dataPointer == 0)
                        {
                            continue;
                        }

                        var name = memory.ReadStringZ(namePointer);

                        tempEnums.Add(new KeyValuePair<string, uint>(name, dataPointer));
                    }
                }

                Directory.CreateDirectory(Path.Combine(outputPath, "enums"));
                foreach (var kv in tempEnums.OrderBy(kv => kv.Key))
                {
                    var name = kv.Key;
                    var pointer = kv.Value;

                    var elements = ReadEnum(memory, pointer);
                    if (elements == null)
                    {
                        continue;
                    }

                    enums.Add(new KeyValuePair<string, uint>(name, pointer));

                    Console.WriteLine("[enum] {0}", name);

                    var settings = new XmlWriterSettings()
                    {
                        Indent = true,
                    };

                    using (var xml = XmlWriter.Create(Path.Combine(outputPath, "enums", name + ".enum.xml"), settings))
                    {
                        xml.WriteStartDocument();
                        xml.WriteStartElement("enum");
                        xml.WriteAttributeString("name", name);

                        ExportEnum(memory, pointer, elements, xml);

                        xml.WriteEndElement();
                        xml.WriteEndDocument();
                    }
                }

                var parsers = new List<KeyValuePair<string, uint>>();
                {
                    var stashPointer = Locator.FindParserTable(memory);
                    var stashCount = memory.ReadS32(stashPointer + 0x08);
                    var stashEntryPointer = memory.ReadU32(stashPointer + 0x14);
                    var stashEntries = memory.ReadBytes(stashEntryPointer, stashCount * 8);

                    for (int i = 0; i < stashCount; i++)
                    {
                        var namePointer = BitConverter.ToUInt32(stashEntries, (i * 8) + 0);
                        var dataPointer = BitConverter.ToUInt32(stashEntries, (i * 8) + 4);

                        if (namePointer == 0 &&
                            dataPointer == 0)
                        {
                            continue;
                        }

                        var name = memory.ReadStringZ(namePointer);

                        parsers.Add(new KeyValuePair<string, uint>(name, dataPointer));
                    }
                }

                Directory.CreateDirectory(Path.Combine(outputPath, "schemas"));
                foreach (var kv in parsers.OrderBy(kv => kv.Key))
                {
                    var name = kv.Key;
                    var pointer = kv.Value;

                    Console.WriteLine("[schema] {0}", name);

                    var settings = new XmlWriterSettings()
                    {
                        Indent = true,
                    };

                    uint? hash = null;

                    /*
                    // disable for now
                    if (dontHash.Contains(name) == false &&
                        name.StartsWith("XML") == false)
                    {
                        hash = HashTable(memory, pointer, 1);
                    }
                    */

                    using (var xml = XmlWriter.Create(Path.Combine(outputPath, "schemas", name + ".schema.xml"), settings))
                    {
                        xml.WriteStartDocument();
                        xml.WriteStartElement("parser");
                        xml.WriteAttributeString("name", name);

                        if (hash != null)
                        {
                            xml.WriteAttributeString("hash", "0x" + hash.Value.Swap().ToString("X8"));
                        }

                        xml.WriteStartElement("table");
                        ExportParserTable(memory, pointer, xml, parsers, enums);
                        xml.WriteEndElement();

                        xml.WriteEndElement();
                        xml.WriteEndDocument();
                    }
                }
            }
        }
    }
}
