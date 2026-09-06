using System;
using System.Text;

namespace AstraSize.Services.Mft
{
    public class RawMftItem
    {
        public ulong RecordNumber;
        public ulong ParentRecordNumber;
        public string Name = string.Empty;
        public long Size;
        public bool IsDirectory;
        public DateTime LastModified;
    }

    public static class MftRecordParser
    {
        private const uint FILE_MAGIC = 0x454C4946; // "FILE"
        private const uint ATTR_STANDARD_INFORMATION = 0x10;
        private const uint ATTR_FILE_NAME = 0x30;
        private const uint ATTR_DATA = 0x80;
        private const uint ATTR_END = 0xFFFFFFFF;

        public static bool TryParseRecord(byte[] record, int offset, int recordSize, ulong fallbackRecordNum, out RawMftItem? item)
        {
            item = null;
            if (record.Length < offset + recordSize) return false;

            // 1. Magic check "FILE"
            uint magic = BitConverter.ToUInt32(record, offset);
            if (magic != FILE_MAGIC) return false;

            // 2. Flags check
            ushort flags = BitConverter.ToUInt16(record, offset + 0x16);
            bool inUse = (flags & 0x01) != 0;
            if (!inUse) return false; // Deleted / unallocated record

            bool isDirectory = (flags & 0x02) != 0;

            // 3. Fixup / Update Sequence Array
            ushort updateSeqOffset = BitConverter.ToUInt16(record, offset + 0x04);
            ushort updateSeqSize = BitConverter.ToUInt16(record, offset + 0x06);

            if (updateSeqOffset + (updateSeqSize * 2) <= recordSize)
            {
                ushort updateSeqNum = BitConverter.ToUInt16(record, offset + updateSeqOffset);
                for (int i = 1; i < updateSeqSize; i++)
                {
                    int sectorEnd = offset + (i * 512) - 2;
                    if (sectorEnd + 2 <= offset + recordSize)
                    {
                        ushort expected = BitConverter.ToUInt16(record, sectorEnd);
                        if (expected == updateSeqNum)
                        {
                            ushort replacement = BitConverter.ToUInt16(record, offset + updateSeqOffset + (i * 2));
                            record[sectorEnd] = (byte)(replacement & 0xFF);
                            record[sectorEnd + 1] = (byte)((replacement >> 8) & 0xFF);
                        }
                    }
                }
            }

            // 4. Record number
            ulong recordNum = fallbackRecordNum;
            if (recordSize >= 0x30)
            {
                uint recNumFromHeader = BitConverter.ToUInt32(record, offset + 0x2C);
                if (recNumFromHeader > 0) recordNum = recNumFromHeader;
            }

            ushort firstAttrOffset = BitConverter.ToUInt16(record, offset + 0x14);
            int attrOffset = offset + firstAttrOffset;

            string bestName = string.Empty;
            byte bestNamespace = 255;
            ulong parentFrn = 0;
            long fileSize = 0;
            DateTime lastModified = DateTime.MinValue;

            while (attrOffset + 8 <= offset + recordSize)
            {
                uint attrType = BitConverter.ToUInt32(record, attrOffset);
                if (attrType == ATTR_END || attrType == 0) break;

                uint attrLength = BitConverter.ToUInt32(record, attrOffset + 4);
                if (attrLength == 0 || attrOffset + attrLength > offset + recordSize) break;

                byte nonResident = record[attrOffset + 8];

                if (attrType == ATTR_FILE_NAME && nonResident == 0)
                {
                    // Resident $FILE_NAME
                    ushort contentOffset = BitConverter.ToUInt16(record, attrOffset + 0x14);
                    int fnStart = attrOffset + contentOffset;

                    if (fnStart + 0x42 <= attrOffset + attrLength)
                    {
                        ulong rawParentFrn = BitConverter.ToUInt64(record, fnStart);
                        ulong parentRecord = rawParentFrn & 0x0000FFFFFFFFFFFF; // Lower 48 bits are record number

                        long modFileTime = BitConverter.ToInt64(record, fnStart + 0x10);
                        if (modFileTime > 0)
                        {
                            try { lastModified = DateTime.FromFileTimeUtc(modFileTime).ToLocalTime(); } catch { }
                        }

                        byte nameLength = record[fnStart + 0x40];
                        byte nameSpace = record[fnStart + 0x41];

                        if (fnStart + 0x42 + (nameLength * 2) <= attrOffset + attrLength)
                        {
                            string candidateName = Encoding.Unicode.GetString(record, fnStart + 0x42, nameLength * 2);

                            // Prefer Win32 (1), Win32&DOS (3), POSIX (0) over DOS-only (2)
                            if (nameSpace != 2 || string.IsNullOrEmpty(bestName))
                            {
                                if (bestNamespace == 255 || (nameSpace != 2 && bestNamespace == 2) || (nameSpace == 1 || nameSpace == 3))
                                {
                                    bestName = candidateName;
                                    bestNamespace = nameSpace;
                                    parentFrn = parentRecord;
                                }
                            }
                        }
                    }
                }
                else if (attrType == ATTR_DATA && !isDirectory)
                {
                    byte nameLen = record[attrOffset + 9];
                    // Unnamed data stream is primary file payload
                    if (nameLen == 0)
                    {
                        if (nonResident == 0)
                        {
                            // Resident: length is at +0x10
                            fileSize = BitConverter.ToUInt32(record, attrOffset + 0x10);
                        }
                        else
                        {
                            // Non-resident: real size is at +0x30
                            if (attrOffset + 0x38 <= offset + recordSize)
                            {
                                fileSize = BitConverter.ToInt64(record, attrOffset + 0x30);
                            }
                        }
                    }
                }

                attrOffset += (int)attrLength;
            }

            if (string.IsNullOrEmpty(bestName)) return false;

            item = new RawMftItem
            {
                RecordNumber = recordNum,
                ParentRecordNumber = parentFrn,
                Name = bestName,
                Size = isDirectory ? 0 : Math.Max(0, fileSize),
                IsDirectory = isDirectory,
                LastModified = lastModified == DateTime.MinValue ? DateTime.Now : lastModified
            };

            return true;
        }
    }
}
