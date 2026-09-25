using System;
using System.Collections.Generic;

namespace AstraSize.Services.Mft
{
    public struct MftExtent
    {
        public long StartLcn;
        public long ClusterCount;
    }

    public static class MftDataRunDecoder
    {
        public static List<MftExtent> DecodeDataRuns(byte[] buffer, int offset, long fallbackStartLcn)
        {
            var extents = new List<MftExtent>();
            // NTFS Runlist: The first LCN delta is relative to LCN 0 (start of volume)
            long currentLcn = 0;

            while (offset < buffer.Length)
            {
                byte header = buffer[offset++];
                if (header == 0) break;

                int lengthBytes = header & 0x0F;
                int offsetBytes = (header >> 4) & 0x0F;

                if (offset + lengthBytes + offsetBytes > buffer.Length) break;

                long length = 0;
                for (int i = 0; i < lengthBytes; i++)
                {
                    length |= ((long)buffer[offset + i]) << (i * 8);
                }
                offset += lengthBytes;

                long lcnDelta = 0;
                if (offsetBytes > 0)
                {
                    for (int i = 0; i < offsetBytes; i++)
                    {
                        lcnDelta |= ((long)buffer[offset + i]) << (i * 8);
                    }
                    // Sign extension for negative delta
                    if ((buffer[offset + offsetBytes - 1] & 0x80) != 0)
                    {
                        for (int i = offsetBytes; i < 8; i++)
                        {
                            lcnDelta |= ((long)0xFF) << (i * 8);
                        }
                    }
                    offset += offsetBytes;
                    currentLcn += lcnDelta;
                }

                extents.Add(new MftExtent
                {
                    StartLcn = currentLcn,
                    ClusterCount = length
                });
            }

            return extents;
        }
    }
}
