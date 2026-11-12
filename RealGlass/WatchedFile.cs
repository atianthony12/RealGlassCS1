using System;
using System.IO;

namespace RealGlass
{
    /// <summary>
    /// A file a player may edit with the game running, as far as this session knows it: its
    /// text, and the write time and length it had when that text was read or written. Used twice:
    /// RealGlass.xml (<see cref="SettingsXml"/>) and the picked profile's file
    /// (<see cref="Profiles"/>). (Volumetric Weather's.)
    /// </summary>
    /// <remarks>
    /// A look once a second compares the time and the length (<see cref="Moved"/>), and only a
    /// file that moved is read. Our own writes are remembered, so they are never mistaken for an
    /// edit; an edit that leaves the text as it was changes nothing.
    /// </remarks>
    internal sealed class WatchedFile
    {
        private DateTime _seenTime;
        private long _seenLength = -1;

        public WatchedFile(string path)
        {
            Path = path;
        }

        public string Path { get; private set; }

        /// <summary>What is on disk as far as this session knows; null when unknown.</summary>
        public string DiskText;

        /// <summary>The file exists and cannot be read. Nothing is written over it until it can.</summary>
        public bool Broken;

        /// <summary>What is now on disk, so our own write is not mistaken for an edit.</summary>
        public void Remember(string text)
        {
            DiskText = text;

            try
            {
                var info = new FileInfo(Path);
                _seenTime = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                _seenLength = info.Exists ? info.Length : -1;
            }
            catch (Exception)
            {
                _seenLength = -1;
            }
        }

        /// <summary>Its write time or length is not what <see cref="Remember"/> saw.</summary>
        public bool Moved(FileInfo info)
        {
            return info.LastWriteTimeUtc != _seenTime || info.Length != _seenLength;
        }
    }
}
