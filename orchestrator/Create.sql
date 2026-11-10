-- Run once against SQL Server before starting CPR
USE [master];
GO

IF DB_ID( N'CPR' ) IS NULL
BEGIN
	CREATE DATABASE [CPR];
END;
GO

USE [CPR];
GO

SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

CREATE TABLE [dbo].[Prompt]
(
	[PromptId] int NOT NULL,
	[Content] nvarchar(max) NOT NULL,
	CONSTRAINT [PK_Prompt] PRIMARY KEY ( [PromptId] )
);

CREATE TABLE [dbo].[Worker]
(
	[WorkerId] int NOT NULL,
	[SshHost] nvarchar(100) NOT NULL,
	[Status] int NOT NULL,
	[LastSeenAt] datetimeoffset NULL,
	CONSTRAINT [PK_Worker] PRIMARY KEY ( [WorkerId] )
);

CREATE TABLE [dbo].[PullRequest]
(
	[PullRequestId] bigint NOT NULL IDENTITY(1, 1),
	[Repository] nvarchar(255) NOT NULL,
	[Number] int NOT NULL,
	[TargetBranch] nvarchar(255) NOT NULL,
	[HeadRepository] nvarchar(255) NOT NULL,
	[HeadBranch] nvarchar(255) NOT NULL,
	[RemoteSha] nvarchar(64) NOT NULL,
	[Status] int NOT NULL,
	[WorkReason] int NOT NULL,
	[WorkerId] int NULL,
	[SessionId] nvarchar(128) NULL,
	[PreparedSha] nvarchar(64) NULL,
	[ApprovedSha] nvarchar(64) NULL,
	[PublishedSha] nvarchar(64) NULL,
	[LastError] nvarchar(max) NULL,
	[CreatedAt] datetimeoffset NOT NULL,
	CONSTRAINT [PK_PullRequest] PRIMARY KEY ( [PullRequestId] ),
	CONSTRAINT [CK_PullRequest_Number] CHECK ( [Number] > 0 ),
	CONSTRAINT [FK_PullRequest_Worker_WorkerId] FOREIGN KEY ( [WorkerId] ) REFERENCES [dbo].[Worker] ( [WorkerId] )
);

CREATE UNIQUE INDEX [IX_PullRequest_Repository_Number] ON [dbo].[PullRequest] ( [Repository], [Number] );
CREATE INDEX [IX_PullRequest_Repository_TargetBranch_PullRequestId] ON [dbo].[PullRequest] ( [Repository], [TargetBranch], [PullRequestId] );
CREATE UNIQUE INDEX [IX_PullRequest_WorkerId] ON [dbo].[PullRequest] ( [WorkerId] ) WHERE [WorkerId] IS NOT NULL;
CREATE UNIQUE INDEX [IX_Worker_SshHost] ON [dbo].[Worker] ( [SshHost] );

-- WorkerStatus: Online = 0, Offline = 1, Paused = 2
INSERT INTO [dbo].[Worker] ( [WorkerId], [SshHost], [Status] )
VALUES
	( 101, N'cpr-101', 1 ),
	( 102, N'cpr-102', 1 ),
	( 103, N'cpr-103', 1 ),
	( 104, N'cpr-104', 1 ),
	( 105, N'cpr-105', 1 ),
	( 106, N'cpr-106', 1 ),
	( 107, N'cpr-107', 1 ),
	( 108, N'cpr-108', 1 ),
	( 109, N'cpr-109', 1 );

-- PromptKind: InitialReview = 0, MergeConflict = 1
INSERT INTO [dbo].[Prompt] ( [PromptId], [Content] )
VALUES
	( 0, N'Review and improve {{PullRequestUrl}} in the already prepared checkout.
Read and follow docs/helpful-pr-review-guidelines.md when present.
Review carefully, repair verified problems, and run relevant tests. Produce a
concise report at /workspace/repo/CPR_REVIEW.md (do not commit this report).
Do not push, merge, commit, alter Git remotes or request additional permissions.
Your changes will be snapshotted for the user''s explicit approval.' ),
	( 1, N'This PR is first in the merge lane for {{Repository}} -> {{TargetBranch}}.
Resolve conflicts against the current base branch for {{PullRequestUrl}}.
Analyze the affected semantics; do NOT blindly prefer ours or theirs.
Execute relevant tests; document conflicts, rationale and any risks in
/workspace/repo/CPR_REVIEW.md (do not commit this report).
Do not push, merge, commit, alter Git remotes or request additional permissions.
Your changes will be snapshotted for the user''s explicit approval.' );

COMMIT TRANSACTION;
GO
