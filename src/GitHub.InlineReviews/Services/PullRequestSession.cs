﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitHub.Extensions;
using GitHub.InlineReviews.Models;
using GitHub.Models;
using GitHub.Services;
using ReactiveUI;
using System.Threading;

namespace GitHub.InlineReviews.Services
{
    /// <summary>
    /// A pull request session used to display inline reviews.
    /// </summary>
    /// <remarks>
    /// A pull request session represents the real-time state of a pull request in the IDE.
    /// It takes the pull request model and updates according to the current state of the
    /// repository on disk and in the editor.
    /// </remarks>
    public class PullRequestSession : ReactiveObject, IPullRequestSession
    {
        static readonly List<IPullRequestReviewCommentModel> Empty = new List<IPullRequestReviewCommentModel>();
        readonly IPullRequestSessionService service;
        readonly Dictionary<string, PullRequestSessionFile> fileIndex = new Dictionary<string, PullRequestSessionFile>();
        readonly SemaphoreSlim getFilesLock = new SemaphoreSlim(1);
        bool isCheckedOut;
        IReadOnlyList<IPullRequestSessionFile> files;

        public PullRequestSession(
            IPullRequestSessionService service,
            IAccount user,
            IPullRequestModel pullRequest,
            ILocalRepositoryModel repository,
            bool isCheckedOut)
        {
            Guard.ArgumentNotNull(service, nameof(service));
            Guard.ArgumentNotNull(user, nameof(user));
            Guard.ArgumentNotNull(pullRequest, nameof(pullRequest));
            Guard.ArgumentNotNull(repository, nameof(repository));

            this.service = service;
            this.isCheckedOut = isCheckedOut;
            User = user;
            PullRequest = pullRequest;
            Repository = repository;
        }

        /// <inheritdoc/>
        public async Task AddComment(IPullRequestReviewCommentModel comment)
        {
            PullRequest.ReviewComments = PullRequest.ReviewComments
                .Concat(new[] { comment })
                .ToList();
            await Update(PullRequest);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<IPullRequestSessionFile>> GetAllFiles()
        {
            if (files == null)
            {
                files = await CreateAllFiles();
            }

            return files;
        }

        /// <inheritdoc/>
        public async Task<IPullRequestSessionFile> GetFile(string relativePath)
        {
            return await GetFile(relativePath, null);
        }

        /// <inheritdoc/>
        public async Task<IPullRequestSessionFile> GetFile(
            string relativePath,
            IEditorContentSource contentSource)
        {
            await getFilesLock.WaitAsync();

            try
            {
                PullRequestSessionFile file;

                relativePath = relativePath.Replace("\\", "/");

                if (!fileIndex.TryGetValue(relativePath, out file))
                {
                    // TODO: Check for binary files.
                    file = await CreateFile(relativePath, contentSource);
                    fileIndex.Add(relativePath, file);
                }
                else if (contentSource != null && file.ContentSource != contentSource)
                {
                    file.ContentSource = contentSource;
                    await UpdateEditorContent(relativePath);
                }

                return file;
            }
            finally
            {
                getFilesLock.Release();
            }
        }

        /// <inheritdoc/>
        public string GetRelativePath(string path)
        {
            if (Path.IsPathRooted(path))
            {
                var basePath = Repository.LocalPath;

                if (path.StartsWith(basePath) && path.Length > basePath.Length + 1)
                {
                    return path.Substring(basePath.Length + 1);
                }
            }

            return null;
        }

        /// <inheritdoc/>
        public async Task UpdateEditorContent(string relativePath)
        {
            PullRequestSessionFile file;

            relativePath = relativePath.Replace("\\", "/");

            if (fileIndex.TryGetValue(relativePath, out file))
            {
                var content = await GetFileContent(file);

                file.CommitSha = await CalculateCommitSha(file, content);
                var mergeBaseSha = await service.GetPullRequestMergeBase(Repository, PullRequest);
                file.Diff = await service.Diff(Repository, mergeBaseSha, relativePath, content);

                foreach (var thread in file.InlineCommentThreads)
                {
                    thread.LineNumber = GetUpdatedLineNumber(thread, file.Diff);
                    thread.IsStale = false;
                }
            }
        }

        public async Task Update(IPullRequestModel pullRequest)
        {
            PullRequest = pullRequest;

            foreach (var file in this.fileIndex.Values.ToList())
            {
                await UpdateFile(file);
            }
        }

        async Task UpdateFile(PullRequestSessionFile file)
        {
            var content = await GetFileContent(file);

            file.BaseSha = PullRequest.Base.Sha;
            file.CommitSha = await CalculateCommitSha(file, content);
            var mergeBaseSha = await service.GetPullRequestMergeBase(Repository, PullRequest);
            file.Diff = await service.Diff(Repository, mergeBaseSha, file.RelativePath, content);

            var commentsByPosition = PullRequest.ReviewComments
                .Where(x => x.Path == file.RelativePath && x.OriginalPosition.HasValue)
                .OrderBy(x => x.Id)
                .GroupBy(x => Tuple.Create(x.OriginalCommitId, x.OriginalPosition.Value));
            var threads = new List<IInlineCommentThreadModel>();

            foreach (var comments in commentsByPosition)
            {
                var hunk = comments.First().DiffHunk;
                var chunks = DiffUtilities.ParseFragment(hunk);
                var chunk = chunks.Last();
                var diffLines = chunk.Lines.Reverse().Take(5).ToList();
                var thread = new InlineCommentThreadModel(
                    file.RelativePath,
                    comments.Key.Item1,
                    comments.Key.Item2,
                    diffLines,
                    comments);

                thread.LineNumber = GetUpdatedLineNumber(thread, file.Diff);
                threads.Add(thread);
            }

            file.InlineCommentThreads = threads;
        }

        async Task<PullRequestSessionFile> CreateFile(
            string relativePath,
            IEditorContentSource contentSource)
        {
            var file = new PullRequestSessionFile(relativePath);
            file.ContentSource = contentSource;
            await UpdateFile(file);
            return file;
        }

        async Task<IReadOnlyList<IPullRequestSessionFile>> CreateAllFiles()
        {
            var result = new List<IPullRequestSessionFile>();

            foreach (var path in FilePaths)
            {
                result.Add(await CreateFile(path, null));
            }

            return result;
        }

        async Task<string> CalculateCommitSha(IPullRequestSessionFile file, byte[] content)
        {
            if (IsCheckedOut)
            {
                return await service.IsUnmodifiedAndPushed(Repository, file.RelativePath, content) ?
                        await service.GetTipSha(Repository) : null;
            }
            else
            {
                return PullRequest.Head.Sha;
            }       
        }

        Task<byte[]> GetFileContent(IPullRequestSessionFile file)
        {
            if (!IsCheckedOut)
            {
                return service.ExtractFileFromGit(
                    Repository,
                    PullRequest.Number,
                    PullRequest.Head.Sha,
                    file.RelativePath);
            }
            else if (file.ContentSource != null)
            {
                return file.ContentSource?.GetContent();
            }
            else
            {
                return service.ReadFileAsync(Path.Combine(Repository.LocalPath, file.RelativePath));
            }
        }

        string GetFullPath(string relativePath)
        {
            return Path.Combine(Repository.LocalPath, relativePath);
        }

        int GetUpdatedLineNumber(IInlineCommentThreadModel thread, IEnumerable<DiffChunk> diff)
        {
            var line = DiffUtilities.Match(diff, thread.DiffMatch);

            if (line != null)
            {
                return (thread.DiffLineType == DiffChangeType.Delete) ?
                    line.OldLineNumber - 1 :
                    line.NewLineNumber - 1;
            }

            return -1;
        }

        /// <inheritdoc/>
        public bool IsCheckedOut
        {
            get { return isCheckedOut; }
            internal set { this.RaiseAndSetIfChanged(ref isCheckedOut, value); }
        }

        /// <inheritdoc/>
        public IAccount User { get; }

        /// <inheritdoc/>
        public IPullRequestModel PullRequest { get; private set; }

        /// <inheritdoc/>
        public ILocalRepositoryModel Repository { get; }

        IEnumerable<string> FilePaths
        {
            get { return PullRequest.ChangedFiles.Select(x => x.FileName); }
        }
    }
}
