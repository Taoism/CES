//============================================================================
// BDInfo - Blu-ray Video and Audio Analysis Tool
// Copyright © 2010 Cinema Squid
//
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
//=============================================================================
//
// Modified for CES, 2026-11-17: the scan, playlist list and ScanBDROMResult
// from FormMain.cs without WinForms (ScanBDROMWork, ScanBDROMThread,
// LoadPlaylists, ComparePlaylistFiles). The scan runs on the caller's thread,
// is cancelled with a CancellationToken and reports progress by callback.
//=============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace BDInfo
{
    public class ScanBDROMResult
    {
        public Exception ScanException = new Exception("Scan has not been run.");
        public Dictionary<string, Exception> FileExceptions = new Dictionary<string, Exception>();
    }

    public static class BDInfoScanner
    {
        /// <summary>
        /// Valid playlists in BDInfo's order: grouped by shared clips, longest first,
        /// as in the BDInfo main window. Short and looping playlists are already
        /// filtered by <see cref="BDInfoSettings"/>.
        /// </summary>
        public static List<TSPlaylistFile> GetPlaylists(BDROM bdrom)
        {
            var groups = new List<List<TSPlaylistFile>>();
            var sorted = new List<TSPlaylistFile>(bdrom.PlaylistFiles.Values);
            sorted.Sort(ComparePlaylistFiles);

            foreach (TSPlaylistFile playlist1 in sorted)
            {
                if (!playlist1.IsValid) continue;

                int matchingGroupIndex = 0;
                for (int groupIndex = 0; groupIndex < groups.Count && matchingGroupIndex == 0; groupIndex++)
                {
                    foreach (TSPlaylistFile playlist2 in groups[groupIndex])
                    {
                        if (playlist2.IsValid && ShareClip(playlist1, playlist2))
                        {
                            matchingGroupIndex = groupIndex + 1;
                            break;
                        }
                    }
                }
                if (matchingGroupIndex > 0)
                    groups[matchingGroupIndex - 1].Add(playlist1);
                else
                    groups.Add(new List<TSPlaylistFile> { playlist1 });
            }

            var result = new List<TSPlaylistFile>();
            foreach (List<TSPlaylistFile> group in groups)
            {
                group.Sort(ComparePlaylistFiles);
                result.AddRange(group);
            }
            return result;
        }

        private static bool ShareClip(TSPlaylistFile a, TSPlaylistFile b)
        {
            foreach (TSStreamClip clip1 in a.StreamClips)
                foreach (TSStreamClip clip2 in b.StreamClips)
                    if (clip1.Name == clip2.Name)
                        return true;
            return false;
        }

        public static int ComparePlaylistFiles(TSPlaylistFile x, TSPlaylistFile y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return 1;
            if (y == null) return -1;
            if (x.TotalLength > y.TotalLength) return -1;
            if (y.TotalLength > x.TotalLength) return 1;
            return string.CompareOrdinal(x.Name, y.Name);
        }

        /// <summary>
        /// Full scan of the stream files used by <paramref name="playlists"/> (all stream
        /// files when null or empty): exact bitrates, stream details, diagnostics.
        /// <paramref name="progress"/> gets (bytes done, bytes total) about twice a second.
        /// Throws <see cref="OperationCanceledException"/> when cancelled.
        /// </summary>
        public static ScanBDROMResult Scan(
            BDROM bdrom,
            IList<TSPlaylistFile> playlists,
            Action<long, long> progress,
            CancellationToken cancel)
        {
            var result = new ScanBDROMResult { ScanException = new Exception("Scan is still running.") };

            var streamFiles = new List<TSStreamFile>();
            if (playlists == null || playlists.Count == 0)
            {
                foreach (TSStreamFile streamFile in bdrom.StreamFiles.Values)
                    if (streamFile != null)
                        streamFiles.Add(streamFile);
            }
            else
            {
                foreach (TSPlaylistFile playlist in playlists)
                    foreach (TSStreamClip clip in playlist.StreamClips)
                        if (clip.StreamFile != null && !streamFiles.Contains(clip.StreamFile))
                            streamFiles.Add(clip.StreamFile);
            }

            foreach (TSPlaylistFile playlist in bdrom.PlaylistFiles.Values)
                playlist.ClearBitrates();

            long totalBytes = 0;
            var playlistMap = new Dictionary<string, List<TSPlaylistFile>>();
            foreach (TSStreamFile streamFile in streamFiles)
            {
                totalBytes += ScanLength(streamFile);
                var list = new List<TSPlaylistFile>();
                foreach (TSPlaylistFile playlist in bdrom.PlaylistFiles.Values)
                    foreach (TSStreamClip clip in playlist.StreamClips)
                        if (clip.Name == streamFile.Name && !list.Contains(playlist))
                            list.Add(playlist);
                playlistMap[streamFile.Name] = list;
            }

            long finishedBytes = 0;
            TSStreamFile current = null;
            using (var timer = progress == null ? null : new Timer(_ =>
                {
                    TSStreamFile file = current;
                    progress(finishedBytes + (file != null ? Math.Max(0, file.Size) : 0), totalBytes);
                }, null, 500, 500))
            {
                foreach (TSStreamFile streamFile in streamFiles)
                {
                    cancel.ThrowIfCancellationRequested();
                    current = streamFile;
                    try
                    {
                        streamFile.Scan(playlistMap[streamFile.Name], true, cancel);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.FileExceptions[streamFile.Name] = ex;
                    }
                    current = null;
                    finishedBytes += ScanLength(streamFile);
                }
            }
            progress?.Invoke(totalBytes, totalBytes);
            result.ScanException = null;
            return result;
        }

        private static long ScanLength(TSStreamFile streamFile)
        {
            if (BDInfoSettings.EnableSSIF && streamFile.InterleavedFile != null)
                return streamFile.InterleavedFile.FileInfo != null
                    ? streamFile.InterleavedFile.FileInfo.Length
                    : streamFile.InterleavedFile.DFileInfo.Length;
            return streamFile.FileInfo != null ? streamFile.FileInfo.Length : streamFile.DFileInfo.Length;
        }
    }
}
