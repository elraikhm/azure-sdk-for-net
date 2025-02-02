﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for
// license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core.Http;
using Azure.Core.Testing;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Common;
using Azure.Storage.Test;
using Azure.Storage.Test.Shared;
using NUnit.Framework;
using TestConstants = Azure.Storage.Test.Constants;

namespace Azure.Storage.Blobs.Test
{
    public class BlobBaseClientTests : BlobTestBase
    {
        public BlobBaseClientTests(bool async)
            : base(async, null /* RecordedTestMode.Record /* to re-record */)
        {
        }

        [Test]
        public void Ctor_ConnectionString()
        {
            var accountName = "accountName";
            var accountKey = Convert.ToBase64String(new byte[] { 0, 1, 2, 3, 4, 5 });

            var credentials = new StorageSharedKeyCredential(accountName, accountKey);
            var blobEndpoint = new Uri("http://127.0.0.1/" + accountName);
            var blobSecondaryEndpoint = new Uri("http://127.0.0.1/" + accountName + "-secondary");

            var connectionString = new StorageConnectionString(credentials, (blobEndpoint, blobSecondaryEndpoint), (default, default), (default, default), (default, default));

            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();

            var blob = this.InstrumentClient(new BlobBaseClient(connectionString.ToString(true), containerName, blobName, this.GetOptions()));

            var builder = new BlobUriBuilder(blob.Uri);

            Assert.AreEqual(containerName, builder.ContainerName);
            Assert.AreEqual(blobName, builder.BlobName);
            Assert.AreEqual("accountName", builder.AccountName);
        }

        #region Sequential Download

        [Test]
        public async Task DownloadAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(Constants.KB);
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                // Act
                var response = await blob.DownloadAsync();

                // Assert
                Assert.AreEqual(data.Length, response.Value.ContentLength);
                var actual = new MemoryStream();
                await response.Value.Content.CopyToAsync(actual);
                TestHelper.AssertSequenceEqual(data, actual.ToArray());
            }
        }

        [Test]
        public async Task DownloadAsync_CPK()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(Constants.KB);
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                blob = this.InstrumentClient(new BlockBlobClient(this.GetHttpsUri(blob.Uri), blob.Pipeline));
                var customerProvidedKey = this.GetCustomerProvidedKey();
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream, customerProvidedKey: customerProvidedKey);
                }

                // Act
                var response = await blob.DownloadAsync(customerProvidedKey: customerProvidedKey);

                // Assert
                Assert.AreEqual(customerProvidedKey.EncryptionKeyHash, response.Value.Properties.EncryptionKeySha256);
            }
        }

        [Test]
        public async Task DownloadAsync_CpkHttpError()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(Constants.KB);
                var httpBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                Assert.AreEqual(Constants.Blob.Http, httpBlob.Uri.Scheme);
                var httpsblob = this.InstrumentClient(new BlockBlobClient(this.GetHttpsUri(httpBlob.Uri), httpBlob.Pipeline));
                var customerProvidedKey = this.GetCustomerProvidedKey();
                using (var stream = new MemoryStream(data))
                {
                    await httpsblob.UploadAsync(stream, customerProvidedKey: customerProvidedKey);
                }

                // Act
                await TestHelper.AssertExpectedExceptionAsync<ArgumentException>(
                    httpBlob.DownloadAsync(customerProvidedKey: customerProvidedKey),
                    actualException => Assert.AreEqual("Cannot use client-provided key without HTTPS.", actualException.Message));
            }
        }

        [Test]
        public async Task DownloadAsync_WithUnreliableConnection()
        {
            // Arrange
            var service = this.InstrumentClient(
                new BlobServiceClient(
                    new Uri(this.TestConfigDefault.BlobServiceEndpoint),
                    new StorageSharedKeyCredential(this.TestConfigDefault.AccountName, this.TestConfigDefault.AccountKey),
                    this.GetFaultyBlobConnectionOptions(
                        raiseAt: 256 * Constants.KB,
                        raise: new Exception("Unexpected"))));

            using (this.GetNewContainer(out var container, service: service))
            {
                var data = this.GetRandomBuffer(Constants.KB);

                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                // Act
                var response = await blob.DownloadAsync();

                // Assert
                Assert.AreEqual(data.Length, response.Value.ContentLength);
                var actual = new MemoryStream();
                await response.Value.Content.CopyToAsync(actual);
                TestHelper.AssertSequenceEqual(data, actual.ToArray());
            }
        }

        [Test]
        public async Task DownloadAsync_Range()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(10 * Constants.KB);
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var offset = Constants.KB;
                var count = 2 * Constants.KB;
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                // Act
                var response = await blob.DownloadAsync(range: new HttpRange(offset, count));

                // Assert
                Assert.AreEqual(count, response.Value.ContentLength);
                var actual = new MemoryStream();
                await response.Value.Content.CopyToAsync(actual);
                Assert.AreEqual(count, actual.Length);
                TestHelper.AssertSequenceEqual(data.Skip(offset).Take(count), actual.ToArray());
            }
        }

        [Test]
        public async Task DownloadAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var data = this.GetRandomBuffer(Constants.KB);
                    var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await blob.UploadAsync(stream);
                    }

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    var response = await blob.DownloadAsync(accessConditions: accessConditions);

                    // Assert
                    Assert.AreEqual(data.Length, response.Value.ContentLength);
                    var actual = new MemoryStream();
                    await response.Value.Content.CopyToAsync(actual);
                    TestHelper.AssertSequenceEqual(data, actual.ToArray());
                }
            }
        }

        [Test]
        public async Task DownloadAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var data = this.GetRandomBuffer(Constants.KB);
                    var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await blob.UploadAsync(stream);
                    }

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.DownloadAsync(accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task DownloadAsync_MD5()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(10 * Constants.KB);
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var offset = Constants.KB;
                var count = 2 * Constants.KB;
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                // Act
                var response = await blob.DownloadAsync(
                    range: new HttpRange(offset, count),
                    rangeGetContentHash: true);

                // Assert
                var expectedMD5 = MD5.Create().ComputeHash(data.Skip(offset).Take(count).ToArray());
                TestHelper.AssertSequenceEqual(expectedMD5, response.Value.ContentHash);
            }
        }

        [Test]
        public async Task DownloadAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.DownloadAsync(),
                    e => Assert.AreEqual("The specified blob does not exist.", e.Message.Split('\n')[0]));
            }
        }

        [Test]
        public async Task DownloadAsync_Overloads()
        {
            using (this.GetNewContainer(out var container))
            {
                var data = this.GetRandomBuffer(Constants.KB);
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                await Verify(await blob.DownloadAsync());
                await Verify(await blob.DownloadAsync(CancellationToken.None));
                await Verify(await blob.DownloadAsync(range: default));

                async Task Verify(Response<BlobDownloadInfo> response)
                {
                    Assert.AreEqual(data.Length, response.Value.ContentLength);
                    using var actual = new MemoryStream();
                    await response.Value.Content.CopyToAsync(actual);
                    TestHelper.AssertSequenceEqual(data, actual.ToArray());
                }
            }
        }
        #endregion Sequential Download

        #region Parallel Download

        private async Task ParallelDownloadFileAndVerify(
            long size,
            long singleBlockThreshold,
            ParallelTransferOptions parallelTransferOptions)
        {
            var data = this.GetRandomBuffer(size);
            var path = Path.GetTempFileName();

            try
            {
                using (this.GetNewContainer(out var container))
                {
                    var name = this.GetNewBlobName();
                    var blob = this.InstrumentClient(container.GetBlobClient(name));

                    using (var stream = new MemoryStream(data))
                    {
                        await blob.UploadAsync(stream);
                    }

                    var destination = new FileInfo(path);

                    await blob.StagedDownloadAsync(
                        destination,
                        singleBlockThreshold: singleBlockThreshold,
                        parallelTransferOptions: parallelTransferOptions
                        );

                    using (var resultStream = destination.OpenRead())
                    {
                        TestHelper.AssertSequenceEqual(data, resultStream.AsBytes());
                    }
                }
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        [TestCase(512)]
        [TestCase(1 * Constants.KB)]
        [TestCase(2 * Constants.KB)]
        [TestCase(4 * Constants.KB)]
        [TestCase(10 * Constants.KB)]
        [TestCase(20 * Constants.KB)]
        [TestCase(30 * Constants.KB)]
        [TestCase(50 * Constants.KB)]
        [TestCase(501 * Constants.KB)]
        public async Task DownloadFileAsync_Parallel_SmallBlobs(long size) =>
            // Use a 1KB threshold so we get a lot of individual blocks
            await this.ParallelDownloadFileAndVerify(size, Constants.KB, new ParallelTransferOptions { MaximumTransferLength = Constants.KB });

        [Test]
        [Category("Live")]
        [TestCase(33 * Constants.MB, 1)]
        [TestCase(33 * Constants.MB, 4)]
        [TestCase(33 * Constants.MB, 8)]
        [TestCase(33 * Constants.MB, 16)]
        [TestCase(33 * Constants.MB, null)]
        [TestCase(257 * Constants.MB, 1)]
        [TestCase(257 * Constants.MB, 4)]
        [TestCase(257 * Constants.MB, 8)]
        [TestCase(257 * Constants.MB, 16)]
        [TestCase(257 * Constants.MB, null)]
        [TestCase(1 * Constants.GB, 1)]
        [TestCase(1 * Constants.GB, 4)]
        [TestCase(1 * Constants.GB, 8)]
        [TestCase(1 * Constants.GB, 16)]
        [TestCase(1 * Constants.GB, null)]
        public async Task DownloadFileAsync_Parallel_LargeBlobs(long size, int? maximumThreadCount)
        {
            // TODO: #6781 We don't want to add 1GB of random data in the recordings
            if (this.Mode == RecordedTestMode.Live)
            {
                await this.ParallelDownloadFileAndVerify(size, 16 * Constants.MB, new ParallelTransferOptions { MaximumThreadCount = maximumThreadCount });
            }
        }

        #endregion Parallel Download

        [Test]
        public async Task StartCopyFromUriAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var srcBlob = await this.GetNewBlobClient(container);
                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                var operation = await destBlob.StartCopyFromUriAsync(srcBlob.Uri);

                // Assert
                // data copied within an account, so copy should be instantaneous
                if (this.Mode == RecordedTestMode.Playback)
                {
                    operation.PollingInterval = TimeSpan.FromMilliseconds(10);
                }
                await operation.WaitCompletionAsync();
                Assert.IsTrue(operation.HasCompleted);
                Assert.IsTrue(operation.HasValue);
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Metadata()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var srcBlob = await this.GetNewBlobClient(container);
                var metadata = this.BuildMetadata();

                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await destBlob.StartCopyFromUriAsync(
                    source: srcBlob.Uri,
                    metadata: metadata);

                // Assert
                var response = await destBlob.GetPropertiesAsync();
                this.AssertMetadataEquality(metadata, response.Value.Metadata);
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Source_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var srcBlob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(srcBlob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(srcBlob, parameters.LeaseId, garbageLeaseId);
                    var sourceAccessConditions = this.BuildAccessConditions(
                        parameters: parameters);

                    var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                    // Act
                    var response = await destBlob.StartCopyFromUriAsync(
                        source: srcBlob.Uri,
                        sourceAccessConditions: sourceAccessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Source_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var srcBlob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(srcBlob, parameters.NoneMatch);

                    var sourceAccessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: false);

                    var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        destBlob.StartCopyFromUriAsync(
                            source: srcBlob.Uri,
                            sourceAccessConditions: sourceAccessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Destination_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var data = this.GetRandomBuffer(Constants.KB);
                    var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await srcBlob.UploadAsync(stream);
                    }
                    var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                    // destBlob needs to exist so we can get its lease and etag
                    using (var stream = new MemoryStream(data))
                    {
                        await destBlob.UploadAsync(stream);
                    }

                    parameters.Match = await this.SetupBlobMatchCondition(destBlob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(destBlob, parameters.LeaseId, garbageLeaseId);

                    var accessConditions = this.BuildAccessConditions(parameters: parameters);

                    // Act
                    var response = await destBlob.StartCopyFromUriAsync(
                        source: srcBlob.Uri,
                        destinationAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Destination_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var data = this.GetRandomBuffer(Constants.KB);
                    var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await srcBlob.UploadAsync(stream);
                    }

                    // destBlob needs to exist so we can get its etag
                    var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await destBlob.UploadAsync(stream);
                    }

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(destBlob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters: parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        destBlob.StartCopyFromUriAsync(
                            source: srcBlob.Uri,
                            destinationAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_AccessTier()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var srcBlob = await this.GetNewBlobClient(container);
                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                var operation = await destBlob.StartCopyFromUriAsync(
                    srcBlob.Uri,
                    accessTier:AccessTier.Cool);

                // Assert
                // data copied within an account, so copy should be instantaneous
                if (this.Mode == RecordedTestMode.Playback)
                {
                    operation.PollingInterval = TimeSpan.FromMilliseconds(10);
                }
                await operation.WaitCompletionAsync();
                Assert.IsTrue(operation.HasCompleted);
                Assert.IsTrue(operation.HasValue);
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    destBlob.StartCopyFromUriAsync(srcBlob.Uri),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_RehydratePriority()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(Constants.KB);
                var data2 = this.GetRandomBuffer(Constants.KB);
                var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await srcBlob.UploadAsync(stream);
                }

                // destBlob needs to exist so we can get its lease and etag
                using (var stream = new MemoryStream(data2))
                {
                    await destBlob.UploadAsync(stream);
                }

                // Act
                var operation = await destBlob.StartCopyFromUriAsync(
                    srcBlob.Uri,
                    accessTier: AccessTier.Archive,
                    rehydratePriority: RehydratePriority.High);

                // Assert
                // data copied within an account, so copy should be instantaneous
                if (this.Mode == RecordedTestMode.Playback)
                {
                    operation.PollingInterval = TimeSpan.FromMilliseconds(10);
                }
                await operation.WaitCompletionAsync();
                Assert.IsTrue(operation.HasCompleted);
                Assert.IsTrue(operation.HasValue);

                // Act
                await destBlob.SetTierAsync(AccessTier.Cool);
                var propertiesResponse = await destBlob.GetPropertiesAsync();

                // Assert
                Assert.AreEqual("rehydrate-pending-to-cool", propertiesResponse.Value.ArchiveStatus);
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_RehydratePriorityFail()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var srcBlob = await this.GetNewBlobClient(container);
                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    destBlob.StartCopyFromUriAsync(
                        srcBlob.Uri,
                        accessTier: AccessTier.Archive,
                        rehydratePriority: "None"),
                    e => Assert.AreEqual("InvalidHeaderValue", e.ErrorCode));
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_AccessTierFail()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var srcBlob = await this.GetNewBlobClient(container);
                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    destBlob.StartCopyFromUriAsync(
                    srcBlob.Uri,
                    accessTier: AccessTier.P20),
                    e => Assert.AreEqual(BlobErrorCode.InvalidHeaderValue.ToString(), e.ErrorCode));
            }
        }

        [Test]
        public async Task AbortCopyFromUriAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                await container.SetAccessPolicyAsync(PublicAccessType.Blob);
                var data = this.GetRandomBuffer(8 * Constants.MB);

                var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await srcBlob.UploadAsync(stream);
                }

                var secondaryService = this.GetServiceClient_SecondaryAccount_SharedKey();
                using (this.GetNewContainer(out var destContainer, service: secondaryService))
                {
                    var destBlob = this.InstrumentClient(destContainer.GetBlockBlobClient(this.GetNewBlobName()));

                    var operation = await destBlob.StartCopyFromUriAsync(srcBlob.Uri);

                    // Act
                    try
                    {
                        var response = await destBlob.AbortCopyFromUriAsync(operation.Id);

                        // Assert
                        Assert.IsNotNull(response.Headers.RequestId);
                    }
                    catch (StorageRequestFailedException e) when (e.ErrorCode == "NoPendingCopyOperation")
                    {
                        this.WarnCopyCompletedTooQuickly();
                    }
                }
            }
        }

        [Test]
        public async Task AbortCopyFromUriAsync_Lease()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                await container.SetAccessPolicyAsync(PublicAccessType.Blob);
                var data = this.GetRandomBuffer(8 * Constants.MB);

                var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await srcBlob.UploadAsync(stream);
                }
                var secondaryService = this.GetServiceClient_SecondaryAccount_SharedKey();
                using (this.GetNewContainer(out var destContainer, service: secondaryService))
                {
                    var destBlob = this.InstrumentClient(destContainer.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await destBlob.UploadAsync(stream);
                    }

                    var duration = LeaseClient.InfiniteLeaseDuration;
                    var lease = this.InstrumentClient(destBlob.GetLeaseClient(this.Recording.Random.NewGuid().ToString()));
                    var leaseResponse = await lease.AcquireAsync(duration);

                    var operation = await destBlob.StartCopyFromUriAsync(
                        source: srcBlob.Uri,
                        destinationAccessConditions: new BlobAccessConditions
                        {
                            LeaseAccessConditions = new LeaseAccessConditions
                            {
                                LeaseId = leaseResponse.Value.LeaseId
                            }
                        });


                    // Act
                    try
                    {
                        var response = await destBlob.AbortCopyFromUriAsync(
                            copyId: operation.Id,
                            leaseAccessConditions: new LeaseAccessConditions
                            {
                                LeaseId = leaseResponse.Value.LeaseId
                            });

                        // Assert
                        Assert.IsNotNull(response.Headers.RequestId);
                    }
                    catch (StorageRequestFailedException e) when (e.ErrorCode == "NoPendingCopyOperation")
                    {
                        this.WarnCopyCompletedTooQuickly();
                    }
                }
            }
        }

        [Test]
        public async Task AbortCopyFromUriAsync_LeaseFail()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                await container.SetAccessPolicyAsync(PublicAccessType.Blob);
                var data = this.GetRandomBuffer(8 * Constants.MB);

                var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await srcBlob.UploadAsync(stream);
                }
                var secondaryService = this.GetServiceClient_SecondaryAccount_SharedKey();
                using (this.GetNewContainer(out var destContainer, service: secondaryService))
                {
                    var destBlob = this.InstrumentClient(destContainer.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await destBlob.UploadAsync(stream);
                    }

                    var operation = await destBlob.StartCopyFromUriAsync(source: srcBlob.Uri);

                    var leaseId = this.Recording.Random.NewGuid().ToString();

                    // Act
                    try
                    {
                        await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                            destBlob.AbortCopyFromUriAsync(
                                copyId: operation.Id,
                                leaseAccessConditions: new LeaseAccessConditions
                                {
                                    LeaseId = leaseId
                                }),
                            e =>
                            {
                                switch (e.ErrorCode)
                                {
                                    case "NoPendingCopyOperation":
                                        this.WarnCopyCompletedTooQuickly();
                                        break;
                                    default:
                                        Assert.AreEqual("LeaseNotPresentWithBlobOperation", e.ErrorCode);
                                        break;
                                }
                            }
                            );
                    }
                    catch (StorageRequestFailedException e) when (e.ErrorCode == "NoPendingCopyOperation")
                    {
                        this.WarnCopyCompletedTooQuickly();
                    }
                }
            }
        }

        [Test]
        public async Task AbortCopyFromUriAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var copyId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.AbortCopyFromUriAsync(copyId),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task DeleteAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                var response = await blob.DeleteAsync();

                // Assert
                Assert.IsNotNull(response.Headers.RequestId);
            }
        }

        [Test]
        public async Task DeleteAsync_Options()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);
                await blob.CreateSnapshotAsync();

                // Act
                await blob.DeleteAsync(deleteOptions: DeleteSnapshotsOption.Only);

                // Assert
                var response = await blob.GetPropertiesAsync();
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task DeleteAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.DeleteAsync(accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task DeleteAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.DeleteAsync(accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task DeleteAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.DeleteAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        //[Test]
        //public async Task DeleteAsync_Batch()
        //{
        //    using (this.GetNewContainer(out var container, serviceUri: this.GetServiceUri_PreviewAccount_SharedKey()))
        //    {
        //        const int blobSize = Constants.KB;
        //        var data = this.GetRandomBuffer(blobSize);

        //        var blob1 = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
        //        using (var stream = new MemoryStream(data))
        //        {
        //            await blob1.UploadAsync(stream);
        //        }

        //        var blob2 = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
        //        using (var stream = new MemoryStream(data))
        //        {
        //            await blob2.UploadAsync(stream);
        //        }

        //        var batch =
        //            blob1.DeleteAsync()
        //            .And(blob2.DeleteAsync())
        //            ;

        //        var result = await batch;

        //        Assert.IsNotNull(result);
        //        Assert.AreEqual(2, result.Length);
        //        Assert.IsNotNull(result[0].RequestId);
        //        Assert.IsNotNull(result[1].RequestId);
        //    }
        //}

        [Test]
        [NonParallelizable]
        public async Task UndeleteAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                await this.EnableSoftDelete();
                try
                {
                    var blob = await this.GetNewBlobClient(container);
                    await blob.DeleteAsync();

                    // Act
                    var response = await blob.UndeleteAsync();

                    // Assert
                    response.Headers.TryGetValue("x-ms-version", out var version);
                    Assert.IsNotNull(version);
                }
                catch (StorageRequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
                {
                    Assert.Inconclusive("Delete may have happened before soft delete was fully enabled!");
                }
                finally
                {
                    // Cleanup
                    await this.DisableSoftDelete();
                }
            }
        }

        [Test]
        public async Task UndeleteAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.UndeleteAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task GetPropertiesAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                var response = await blob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_CPK()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetAppendBlobClient(this.GetNewBlobName()));
                blob = this.InstrumentClient(new AppendBlobClient(this.GetHttpsUri(blob.Uri), blob.Pipeline));
                var customerProvidedKey = this.GetCustomerProvidedKey();
                await blob.CreateAsync(customerProvidedKey: customerProvidedKey);

                // Act
                var response = await blob.GetPropertiesAsync(customerProvidedKey: customerProvidedKey);

                // Assert
                Assert.AreEqual(customerProvidedKey.EncryptionKeyHash, response.Value.EncryptionKeySha256);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_CpkError()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var httpBlob = this.InstrumentClient(container.GetAppendBlobClient(this.GetNewBlobName()));
                Assert.AreEqual(Constants.Blob.Http, httpBlob.Uri.Scheme);
                var httpsBlob = this.InstrumentClient(new AppendBlobClient(this.GetHttpsUri(httpBlob.Uri), httpBlob.Pipeline));
                var customerProvidedKey = this.GetCustomerProvidedKey();
                await httpsBlob.CreateAsync(customerProvidedKey: customerProvidedKey);

                // Act
                await TestHelper.AssertExpectedExceptionAsync<ArgumentException>(
                    httpBlob.GetPropertiesAsync(customerProvidedKey: customerProvidedKey),
                    actualException => Assert.AreEqual("Cannot use client-provided key without HTTPS.", actualException.Message));
            }
        }

        [Test]
        public async Task GetPropertiesAsync_ContainerSAS()
        {
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);

                var sasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceSas_Container(
                        containerName: containerName)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName));

                // Act
                var response = await sasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        [Ignore("#7309 - upgrading to 2019-02-02")]
        public async Task GetPropertiesAsync_ContainerIdentitySAS()
        {
            var oauthService = this.GetServiceClient_OauthAccount();
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName, service: oauthService))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);

                var userDelegationKey = await oauthService.GetUserDelegationKeyAsync(
                    start: null,
                    expiry: this.Recording.UtcNow.AddHours(1));

                var identitySasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceIdentitySas_Container(
                        containerName: containerName,
                        userDelegationKey: userDelegationKey)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName));

                // Act
                var response = await identitySasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_BlobSAS()
        {
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);

                var sasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceSas_Blob(
                        containerName: containerName,
                        blobName: blobName)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName));

                // Act
                var response = await sasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        [Ignore("#7309 - upgrading to 2019-02-02")]
        public async Task GetPropertiesAsync_BlobIdentitySAS()
        {
            var oauthService = this.GetServiceClient_OauthAccount();
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName, service: oauthService))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);

                var userDelegationKey = await oauthService.GetUserDelegationKeyAsync(
                    start: null,
                    expiry: this.Recording.UtcNow.AddHours(1));

                var identitySasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceIdentitySas_Blob(
                        containerName: containerName,
                        blobName: blobName,
                        userDelegationKey: userDelegationKey)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName));

                // Act
                var response = await identitySasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_SnapshotSAS()
        {
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);
                var snapshotResponse = await blob.CreateSnapshotAsync();

                var sasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceSas_Snapshot(
                        containerName: containerName,
                        blobName: blobName,
                        snapshot: snapshotResponse.Value.Snapshot)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName)
                    .WithSnapshot(snapshotResponse.Value.Snapshot));

                // Act
                var response = await sasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        [Ignore("#7309 - upgrading to 2019-02-02")]
        public async Task GetPropertiesAsync_SnapshotIdentitySAS()
        {
            var oauthService = this.GetServiceClient_OauthAccount();
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName, service: oauthService))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);
                var snapshotResponse = await blob.CreateSnapshotAsync();

                var userDelegationKey = await oauthService.GetUserDelegationKeyAsync(
                    start: null,
                    expiry: this.Recording.UtcNow.AddHours(1));

                var identitySasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceIdentitySas_Container(
                        containerName: containerName,
                        userDelegationKey: userDelegationKey)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName)
                    .WithSnapshot(snapshotResponse.Value.Snapshot));

                // Act
                var response = await identitySasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.GetPropertiesAsync(accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task GetPropertiesAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.GetPropertiesAsync(accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task GetPropertiesAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.GetPropertiesAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task SetHttpHeadersAsync()
        {
            var constants = new TestConstants(this);
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                await blob.SetHttpHeadersAsync(new BlobHttpHeaders
                {
                    CacheControl = constants.CacheControl,
                    ContentDisposition = constants.ContentDisposition,
                    ContentEncoding = new string[] { constants.ContentEncoding },
                    ContentLanguage = new string[] { constants.ContentLanguage },
                    ContentHash = constants.ContentMD5,
                    ContentType = constants.ContentType
                });

                // Assert
                var response = await blob.GetPropertiesAsync();
                Assert.AreEqual(constants.ContentType, response.Value.ContentType);
                TestHelper.AssertSequenceEqual(constants.ContentMD5, response.Value.ContentHash);
                Assert.AreEqual(1, response.Value.ContentEncoding.Count());
                Assert.AreEqual(constants.ContentEncoding, response.Value.ContentEncoding.First());
                Assert.AreEqual(1, response.Value.ContentLanguage.Count());
                Assert.AreEqual(constants.ContentLanguage, response.Value.ContentLanguage.First());
                Assert.AreEqual(constants.ContentDisposition, response.Value.ContentDisposition);
                Assert.AreEqual(constants.CacheControl, response.Value.CacheControl);
            }
        }

        [Test]
        public async Task SetHttpHeadersAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.SetHttpHeadersAsync(
                        httpHeaders: new BlobHttpHeaders(),
                        accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task SetHttpHeadersAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.SetHttpHeadersAsync(
                            httpHeaders: new BlobHttpHeaders(),
                            accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task SetHttpHeadersAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.SetHttpHeadersAsync(new BlobHttpHeaders()),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task SetMetadataAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);
                var metadata = this.BuildMetadata();

                // Act
                await blob.SetMetadataAsync(metadata);

                // Assert
                var response = await blob.GetPropertiesAsync();
                this.AssertMetadataEquality(metadata, response.Value.Metadata);
            }
        }

        [Test]
        public async Task SetMetadataAsync_CPK()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetAppendBlobClient(this.GetNewBlobName()));
                blob = this.InstrumentClient(new AppendBlobClient(this.GetHttpsUri(blob.Uri), blob.Pipeline));
                var customerProvidedKey = this.GetCustomerProvidedKey();
                var metadata = this.BuildMetadata();
                await blob.CreateAsync(customerProvidedKey: customerProvidedKey);

                // Act
                await blob.SetMetadataAsync(metadata, customerProvidedKey: customerProvidedKey);
            }
        }

        [Test]
        public async Task SetMetadataAsync_CpkError()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var httpBlob = this.InstrumentClient(container.GetAppendBlobClient(this.GetNewBlobName()));
                Assert.AreEqual(Constants.Blob.Http, httpBlob.Uri.Scheme);
                var httpsBlob = this.InstrumentClient(new AppendBlobClient(this.GetHttpsUri(httpBlob.Uri), httpBlob.Pipeline));
                var customerProvidedKey = this.GetCustomerProvidedKey();
                var metadata = this.BuildMetadata();
                await httpsBlob.CreateAsync(customerProvidedKey: customerProvidedKey);

                // Act
                await TestHelper.AssertExpectedExceptionAsync<ArgumentException>(
                    httpBlob.SetMetadataAsync(metadata, customerProvidedKey: customerProvidedKey),
                    actualException => Assert.AreEqual("Cannot use client-provided key without HTTPS.", actualException.Message));
            }
        }

        [Test]
        public async Task SetMetadataAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);
                    var metadata = this.BuildMetadata();

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.SetMetadataAsync(
                        metadata: metadata,
                        accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task SetMetadataAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);
                    var metadata = this.BuildMetadata();

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.SetMetadataAsync(
                            metadata: metadata,
                            accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task SetMetadataAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var metadata = this.BuildMetadata();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.SetMetadataAsync(metadata),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task CreateSnapshotAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                var response = await blob.CreateSnapshotAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_CPK()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetAppendBlobClient(this.GetNewBlobName()));
                blob = this.InstrumentClient(new AppendBlobClient(this.GetHttpsUri(blob.Uri), blob.Pipeline));
                var customerProvidedKey = this.GetCustomerProvidedKey();
                await blob.CreateAsync(customerProvidedKey: customerProvidedKey);

                // Act
                var response = await blob.CreateSnapshotAsync(customerProvidedKey: customerProvidedKey);

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_CpkHttpError()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var httpBlob = this.InstrumentClient(container.GetAppendBlobClient(this.GetNewBlobName()));
                Assert.AreEqual(Constants.Blob.Http, httpBlob.Uri.Scheme);
                var httpsBlob = this.InstrumentClient(new AppendBlobClient(this.GetHttpsUri(httpBlob.Uri), httpBlob.Pipeline));
                var customerProvidedKey = this.GetCustomerProvidedKey();
                await httpsBlob.CreateAsync(customerProvidedKey: customerProvidedKey);

                // Act
                await TestHelper.AssertExpectedExceptionAsync<ArgumentException>(
                    httpBlob.CreateSnapshotAsync(customerProvidedKey: customerProvidedKey),
                    actualException => Assert.AreEqual("Cannot use client-provided key without HTTPS.", actualException.Message));
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.CreateSnapshotAsync(accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.CreateSnapshotAsync(accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.CreateSnapshotAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task AcquireLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = TimeSpan.FromSeconds(15);

                // Act
                var response = await this.InstrumentClient(blob.GetLeaseClient(leaseId)).AcquireAsync(duration);

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task AcquireLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    // Act
                    var response = await this.InstrumentClient(blob.GetLeaseClient(leaseId)).AcquireAsync(
                        duration: duration,
                        httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task AcquireLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        this.InstrumentClient(blob.GetLeaseClient(leaseId)).AcquireAsync(
                            duration: duration,
                            httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task AcquireLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = TimeSpan.FromSeconds(15);

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    this.InstrumentClient(blob.GetLeaseClient(leaseId)).AcquireAsync(duration),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task RenewLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = TimeSpan.FromSeconds(15);

                var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                await lease.AcquireAsync(duration);

                // Act
                var response = await lease.RenewAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task RenewLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                    await lease.AcquireAsync(duration: duration);

                    // Act
                    var response = await lease.RenewAsync(httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task RenewLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                    await lease.AcquireAsync(duration: duration);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        lease.RenewAsync(httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task RenewLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    this.InstrumentClient(blob.GetLeaseClient(leaseId)).ReleaseAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task ReleaseLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = TimeSpan.FromSeconds(15);

                var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                await lease.AcquireAsync(duration);

                // Act
                var response = await lease.ReleaseAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task ReleaseLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                    await lease.AcquireAsync(duration: duration);

                    // Act
                    var response = await lease.ReleaseAsync(httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task ReleaseLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                    await lease.AcquireAsync(duration: duration);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        lease.ReleaseAsync(httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task ReleaseLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    this.InstrumentClient(blob.GetLeaseClient(leaseId)).RenewAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task BreakLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = TimeSpan.FromSeconds(15);

                var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                await lease.AcquireAsync(duration);

                // Act
                var response = await lease.BreakAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task BreakLeaseAsync_BreakPeriod()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = TimeSpan.FromSeconds(15);
                var breakPeriod = 5;

                var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                await lease.AcquireAsync(duration);

                // Act
                var response = await lease.BreakAsync(breakPeriodInSeconds: breakPeriod);

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task BreakLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                    await lease.AcquireAsync(duration: duration);

                    // Act
                    var response = await lease.BreakAsync(httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task BreakLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                    await lease.AcquireAsync(duration: duration);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        lease.BreakAsync(httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task BreakLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    this.InstrumentClient(blob.GetLeaseClient()).BreakAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task ChangeLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var newLeaseId = this.Recording.Random.NewGuid().ToString();
                var duration = TimeSpan.FromSeconds(15);

                var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                await lease.AcquireAsync(duration);

                // Act
                var response = await lease.ChangeAsync(newLeaseId);

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task ChangeLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var newLeaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                    await lease.AcquireAsync(duration: duration);

                    // Act
                    var response = await lease.ChangeAsync(
                        proposedId: newLeaseId,
                        httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task ChangeLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var newLeaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = TimeSpan.FromSeconds(15);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    var lease = this.InstrumentClient(blob.GetLeaseClient(leaseId));
                    await lease.AcquireAsync(duration: duration);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        lease.ChangeAsync(
                            proposedId: newLeaseId,
                            httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task ChangeLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();
                var newLeaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    this.InstrumentClient(blob.GetLeaseClient(leaseId)).ChangeAsync(proposedId: newLeaseId),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task SetTierAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                var response = await blob.SetTierAsync(AccessTier.Cool);

                // Assert
                Assert.IsNotNull(response.Headers.RequestId);
            }
        }

        [Test]
        public async Task SetTierAsync_Lease()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = TimeSpan.FromSeconds(15);

                await this.InstrumentClient(blob.GetLeaseClient(leaseId)).AcquireAsync(duration);

                // Act
                var response = await blob.SetTierAsync(
                    accessTier: AccessTier.Cool,
                    leaseAccessConditions: new LeaseAccessConditions
                    {
                        LeaseId = leaseId
                    });

                // Assert
                Assert.IsNotNull(response.Headers.RequestId);
            }
        }

        [Test]
        public async Task SetTierAsync_LeaseFail()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(Constants.KB);

                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                var leaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.SetTierAsync(
                        accessTier: AccessTier.Cool,
                        leaseAccessConditions: new LeaseAccessConditions
                        {
                            LeaseId = leaseId
                        }),
                    e => Assert.AreEqual("LeaseNotPresentWithBlobOperation", e.ErrorCode));
            }
        }

        [Test]
        public async Task SetTierAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();
                var newLeaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.SetTierAsync(AccessTier.Cool),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task SetTierAsync_Rehydrate()
        {
            using (this.GetNewContainer(out var container))
            {
                // arrange
                var blob = await this.GetNewBlobClient(container);
                await blob.SetTierAsync(AccessTier.Archive);

                // Act
                var setTierResponse = await blob.SetTierAsync(
                    accessTier: AccessTier.Cool,
                    rehydratePriority: RehydratePriority.High);
                var propertiesResponse = await blob.GetPropertiesAsync();

                // Assert
                Assert.AreEqual("rehydrate-pending-to-cool", propertiesResponse.Value.ArchiveStatus);
            }
        }

        [Test]
        public async Task SetTierAsync_RehydrateFail()
        {
            using (this.GetNewContainer(out var container))
            {

                // arrange
                var blob = await this.GetNewBlobClient(container);
                await blob.SetTierAsync(AccessTier.Archive);

                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.SetTierAsync(accessTier: AccessTier.Cool, rehydratePriority: "None"),
                    e => Assert.AreEqual("InvalidHeaderValue", e.ErrorCode));
            }
        }

        //[Test]
        //public async Task SetTierAsync_Batch()
        //{
        //    using (this.GetNewContainer(out var container, service: this.GetServiceClient_PreviewAccount_SharedKey()))
        //    {
        //        const int blobSize = Constants.KB;
        //        var data = this.GetRandomBuffer(blobSize);

        //        var blob1 = this.InstrumentClient(container.CreateBlockBlobClient(this.GetNewBlobName()));
        //        using (var stream = new MemoryStream(data))
        //        {
        //            await blob1.UploadAsync(stream);
        //        }

        //        var blob2 = this.InstrumentClient(container.CreateBlockBlobClient(this.GetNewBlobName()));
        //        using (var stream = new MemoryStream(data))
        //        {
        //            await blob2.UploadAsync(stream);
        //        }

        //        var batch =
        //            blob1.SetTierAsync(AccessTier.Cool)
        //            .And(blob2.SetTierAsync(AccessTier.Cool))
        //            ;

        //        var result = await batch;

        //        Assert.IsNotNull(result);
        //        Assert.AreEqual(2, result.Length);
        //        Assert.IsNotNull(result[0].RequestId);
        //        Assert.IsNotNull(result[1].RequestId);
        //    }
        //}

        [Test]
        public void WithSnapshot()
        {
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();

            var service = this.GetServiceClient_SharedKey();

            var container = this.InstrumentClient(service.GetBlobContainerClient(containerName));

            var blob = this.InstrumentClient(container.GetBlockBlobClient(blobName));

            var builder = new BlobUriBuilder(blob.Uri);

            Assert.AreEqual("", builder.Snapshot);

            blob = this.InstrumentClient(blob.WithSnapshot("foo"));

            builder = new BlobUriBuilder(blob.Uri);

            Assert.AreEqual("foo", builder.Snapshot);

            blob = this.InstrumentClient(blob.WithSnapshot(null));

            builder = new BlobUriBuilder(blob.Uri);

            Assert.AreEqual("", builder.Snapshot);
        }

        private async Task<BlobBaseClient> GetNewBlobClient(BlobContainerClient container, string blobName = default)
        {
            blobName = blobName ?? this.GetNewBlobName();
            var blob = this.InstrumentClient(container.GetBlockBlobClient(blobName));
            var data = this.GetRandomBuffer(Constants.KB);

            using (var stream = new MemoryStream(data))
            {
                await blob.UploadAsync(stream);
            }
            return blob;
        }

        public IEnumerable<AccessConditionParameters> AccessConditions_Data
            => new[]
            {
                new AccessConditionParameters(),
                new AccessConditionParameters { IfModifiedSince = this.OldDate },
                new AccessConditionParameters { IfUnmodifiedSince = this.NewDate },
                new AccessConditionParameters { Match = this.ReceivedETag },
                new AccessConditionParameters { NoneMatch = this.GarbageETag },
                new AccessConditionParameters { LeaseId = this.ReceivedLeaseId }
            };

        public IEnumerable<AccessConditionParameters> GetAccessConditionsFail_Data(string garbageLeaseId)
            => new[]
            {
                new AccessConditionParameters { IfModifiedSince = this.NewDate },
                new AccessConditionParameters { IfUnmodifiedSince = this.OldDate },
                new AccessConditionParameters { Match = this.GarbageETag },
                new AccessConditionParameters { NoneMatch = this.ReceivedETag },
                new AccessConditionParameters { LeaseId = garbageLeaseId },
             };

        public IEnumerable<AccessConditionParameters> NoLease_AccessConditions_Data
            => new[]
            {
                new AccessConditionParameters(),
                new AccessConditionParameters { IfModifiedSince = this.OldDate },
                new AccessConditionParameters { IfUnmodifiedSince = this.NewDate },
                new AccessConditionParameters { Match = this.ReceivedETag },
                new AccessConditionParameters { NoneMatch = this.GarbageETag },
            };

        public IEnumerable<AccessConditionParameters> NoLease_AccessConditionsFail_Data
            => new[]
            {
                new AccessConditionParameters { IfModifiedSince = this.NewDate },
                new AccessConditionParameters { IfUnmodifiedSince = this.OldDate },
                new AccessConditionParameters { Match = this.GarbageETag },
                new AccessConditionParameters { NoneMatch = this.ReceivedETag },
            };

        private HttpAccessConditions BuildHttpAccessConditions(
            AccessConditionParameters parameters)
            => new HttpAccessConditions
            {
                IfModifiedSince = parameters.IfModifiedSince,
                IfUnmodifiedSince = parameters.IfUnmodifiedSince,
                IfMatch = parameters.Match != null ? new ETag(parameters.Match) : default(ETag?),
                IfNoneMatch = parameters.NoneMatch != null ? new ETag(parameters.NoneMatch) : default(ETag?)
            };

        private BlobAccessConditions BuildAccessConditions(
            AccessConditionParameters parameters,
            bool lease = true)
        {
            var accessConditions = new BlobAccessConditions
            {
                HttpAccessConditions = this.BuildHttpAccessConditions(parameters)
            };
            if(lease)
            {
                accessConditions.LeaseAccessConditions = new LeaseAccessConditions
                {
                    LeaseId = parameters.LeaseId
                };
            }
            return accessConditions;
        }

        public class AccessConditionParameters
        {
            public DateTimeOffset? IfModifiedSince { get; set; }
            public DateTimeOffset? IfUnmodifiedSince { get; set; }
            public string Match { get; set; }
            public string NoneMatch { get; set; }
            public string LeaseId { get; set; }
        }
    }
}
