﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode.Converter.Internal;
using YoutubeExplode.Models.MediaStreams;

namespace YoutubeExplode.Converter
{
    /// <summary>
    /// The entry point for <see cref="Converter"/>.
    /// </summary>
    public partial class YoutubeConverter : IYoutubeConverter
    {
        private readonly IYoutubeClient _youtubeClient;
        private readonly FfmpegCli _ffmpeg;

        /// <summary>
        /// Creates an instance of <see cref="YoutubeConverter"/>.
        /// </summary>
        public YoutubeConverter(IYoutubeClient youtubeClient, string ffmpegFilePath)
        {
            _youtubeClient = youtubeClient.GuardNotNull(nameof(youtubeClient));

            ffmpegFilePath.GuardNotNull(nameof(ffmpegFilePath));
            _ffmpeg = new FfmpegCli(ffmpegFilePath);
        }

        /// <summary>
        /// Creates an instance of <see cref="YoutubeConverter"/>.
        /// </summary>
        public YoutubeConverter(IYoutubeClient youtubeClient)
            : this(youtubeClient, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg"))
        {
        }

        /// <summary>
        /// Creates an instance of <see cref="YoutubeConverter"/>.
        /// </summary>
        public YoutubeConverter()
            : this(new YoutubeClient())
        {
        }

        /// <inheritdoc />
        public async Task DownloadAndProcessMediaStreamsAsync(IReadOnlyList<MediaStreamInfo> mediaStreamInfos,
            string filePath, string format,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            mediaStreamInfos.GuardNotNull(nameof(mediaStreamInfos));
            filePath.GuardNotNull(nameof(filePath));
            format.GuardNotNull(nameof(format));

            // Determine if transcoding is required - if one of the input stream containers doesn't match the output format
            var transcode = mediaStreamInfos
                .Select(s => s.Container.GetFileExtension())
                .Any(f => !string.Equals(f, format, StringComparison.OrdinalIgnoreCase));

            // Set up progress-related stuff
            var progressMixer = progress != null ? new ProgressMixer(progress) : null;
            var downloadProgressPortion = transcode ? 0.15 : 0.99;
            var ffmpegProgressPortion = 1 - downloadProgressPortion;
            var totalContentLength = mediaStreamInfos.Sum(s => s.Size);

            // Keep track of the downloaded streams
            var streamFilePaths = new List<string>();
            try
            {
                // Download all streams
                foreach (var streamInfo in mediaStreamInfos)
                {
                    // Generate file path
                    var streamIndex = streamFilePaths.Count + 1;
                    var streamFilePath = $"{filePath}.stream-{streamIndex}.tmp";

                    // Add file path to list
                    streamFilePaths.Add(streamFilePath);

                    // Set up download progress handler
                    var streamDownloadProgress =
                        progressMixer?.Split(downloadProgressPortion * streamInfo.Size / totalContentLength);

                    // Download stream
                    await _youtubeClient
                        .DownloadMediaStreamAsync(streamInfo, streamFilePath, streamDownloadProgress, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Set up process progress handler
                var ffmpegProgress = progressMixer?.Split(ffmpegProgressPortion);

                // Process streams (mux/transcode/etc)
                await _ffmpeg
                    .ProcessAsync(streamFilePaths, filePath, format, transcode, ffmpegProgress, cancellationToken)
                    .ConfigureAwait(false);

                // Report completion in case there are rounding issues in progress reporting
                progress?.Report(1);
            }
            finally
            {
                // Delete all stream files
                foreach (var streamFilePath in streamFilePaths)
                    FileEx.TryDelete(streamFilePath);
            }
        }

        /// <inheritdoc />
        public async Task DownloadVideoAsync(MediaStreamInfoSet mediaStreamInfoSet, string filePath, string format,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            mediaStreamInfoSet.GuardNotNull(nameof(mediaStreamInfoSet));
            filePath.GuardNotNull(nameof(filePath));
            format.GuardNotNull(nameof(format));

            var mediaStreamInfos = new List<MediaStreamInfo>();

            // Get best audio stream
            var audioStreamInfo = mediaStreamInfoSet.Audio
                .Where(s => s.Container == Container.Mp4) // only mp4 to make encoding easier
                .OrderByDescending(s => s.Bitrate) // order by bitrate
                .FirstOrDefault(); // take highest bitrate

            // Add to result
            mediaStreamInfos.Add(audioStreamInfo);

            // If needs video - get best video stream
            if (!IsAudioOnlyFormat(format))
            {
                var videoStreamInfo = mediaStreamInfoSet.Video
                    .Where(s => s.Container == Container.Mp4) // only mp4 to make encoding easier
                    .OrderByDescending(s => s.VideoQuality) // order by video quality
                    .ThenByDescending(s => s.Framerate) // order by framerate
                    .ThenBy(s => s.Size) // order by size
                    .FirstOrDefault(); // take smallest size, highest video quality and framerate

                // Add to result
                mediaStreamInfos.Add(videoStreamInfo);
            }

            // Download media streams and process them
            await DownloadAndProcessMediaStreamsAsync(mediaStreamInfos, filePath, format, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DownloadVideoAsync(string videoId, string filePath, string format,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            videoId.GuardNotNull(nameof(videoId));
            filePath.GuardNotNull(nameof(filePath));
            format.GuardNotNull(nameof(format));

            // Get stream info set
            var mediaStreamInfoSet = await _youtubeClient.GetVideoMediaStreamInfosAsync(videoId)
                .ConfigureAwait(false);

            // Download video with known stream info set
            await DownloadVideoAsync(mediaStreamInfoSet, filePath, format, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task DownloadVideoAsync(string videoId, string filePath,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            videoId.GuardNotNull(nameof(videoId));
            filePath.GuardNotNull(nameof(filePath));

            // Determine output file format from extension
            var format = Path.GetExtension(filePath)?.TrimStart('.');

            // If no extension is set - default to mp4 format
            if (format.IsBlank())
                format = "mp4";

            // Download video with known format
            return DownloadVideoAsync(videoId, filePath, format, progress, cancellationToken);
        }
    }

    public partial class YoutubeConverter
    {
        private static readonly string[] AudioOnlyFormats = {"mp3", "m4a", "wav", "wma", "ogg", "aac", "opus"};

        private static bool IsAudioOnlyFormat(string format) =>
            AudioOnlyFormats.Contains(format, StringComparer.OrdinalIgnoreCase);
    }
}