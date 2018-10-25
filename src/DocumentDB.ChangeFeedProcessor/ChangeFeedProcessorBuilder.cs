﻿//----------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  Licensed under the MIT license.
//----------------------------------------------------------------

namespace Microsoft.Azure.Documents.ChangeFeedProcessor
{
    using System;
    using System.Globalization;
    using System.Threading.Tasks;
    using Microsoft.Azure.Documents.ChangeFeedProcessor.Bootstrapping;
    using Microsoft.Azure.Documents.ChangeFeedProcessor.DataAccess;
    using Microsoft.Azure.Documents.ChangeFeedProcessor.FeedProcessing;
    using Microsoft.Azure.Documents.ChangeFeedProcessor.LeaseManagement;
    using Microsoft.Azure.Documents.ChangeFeedProcessor.Logging;
    using Microsoft.Azure.Documents.ChangeFeedProcessor.Monitoring;
    using Microsoft.Azure.Documents.ChangeFeedProcessor.PartitionManagement;
    using Microsoft.Azure.Documents.ChangeFeedProcessor.Utils;
    using Microsoft.Azure.Documents.Client;

    /// <summary>
    /// Provides a flexible way to to create an instance of <see cref="IChangeFeedProcessor"/> with custom set of parameters.
    /// </summary>
    /// <example>
    /// <code language="C#">
    /// <![CDATA[
    /// // Observer.cs
    /// namespace Sample
    /// {
    ///     using System;
    ///     using System.Collections.Generic;
    ///     using System.Threading;
    ///     using System.Threading.Tasks;
    ///     using Microsoft.Azure.Documents;
    ///     using Microsoft.Azure.Documents.ChangeFeedProcessor.FeedProcessing;
    ///
    ///     class SampleObserver : IChangeFeedObserver
    ///     {
    ///         public Task CloseAsync(IChangeFeedObserverContext context, ChangeFeedObserverCloseReason reason)
    ///         {
    ///             return Task.CompletedTask;  // Note: requires targeting .Net 4.6+.
    ///         }
    ///
    ///         public Task OpenAsync(IChangeFeedObserverContext context)
    ///         {
    ///             return Task.CompletedTask;
    ///         }
    ///
    ///         public Task ProcessChangesAsync(IChangeFeedObserverContext context, IReadOnlyList<Document> docs, CancellationToken cancellationToken)
    ///         {
    ///             Console.WriteLine("ProcessChangesAsync: partition {0}, {1} docs", context.PartitionKeyRangeId, docs.Count);
    ///             return Task.CompletedTask;
    ///         }
    ///     }
    /// }
    ///
    /// // Main.cs
    /// namespace Sample
    /// {
    ///     using System;
    ///     using System.Threading.Tasks;
    ///     using Microsoft.Azure.Documents.ChangeFeedProcessor;
    ///     using Microsoft.Azure.Documents.ChangeFeedProcessor.Logging;
    ///
    ///     class ChangeFeedProcessorSample
    ///     {
    ///         public static void Run()
    ///         {
    ///             RunAsync().Wait();
    ///         }
    ///
    ///         static async Task RunAsync()
    ///         {
    ///             DocumentCollectionInfo feedCollectionInfo = new DocumentCollectionInfo()
    ///             {
    ///                 DatabaseName = "DatabaseName",
    ///                 CollectionName = "MonitoredCollectionName",
    ///                 Uri = new Uri("https://sampleservice.documents.azure.com:443/"),
    ///                 MasterKey = "-- the auth key"
    ///             };
    ///
    ///             DocumentCollectionInfo leaseCollectionInfo = new DocumentCollectionInfo()
    ///             {
    ///                 DatabaseName = "DatabaseName",
    ///                 CollectionName = "leases",
    ///                 Uri = new Uri("https://sampleservice.documents.azure.com:443/"),
    ///                 MasterKey = "-- the auth key"
    ///             };
    ///
    ///             var builder = new ChangeFeedProcessorBuilder();
    ///             var processor = await builder
    ///                 .WithHostName("SampleHost")
    ///                 .WithFeedCollection(feedCollectionInfo)
    ///                 .WithLeaseCollection(leaseCollectionInfo)
    ///                 .WithObserver<SampleObserver>()
    ///                 .BuildAsync();
    ///
    ///             await processor.StartAsync();
    ///
    ///             Console.WriteLine("Change Feed Processor started. Press <Enter> key to stop...");
    ///             Console.ReadLine();
    ///
    ///             await processor.StopAsync();
    ///         }
    ///     }
    /// }
    /// ]]>
    /// </code>
    /// </example>
    public class ChangeFeedProcessorBuilder
    {
        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();
        private static readonly long DefaultUnhealthinessDuration = TimeSpan.FromMinutes(15).Ticks;
        private readonly TimeSpan sleepTime = TimeSpan.FromSeconds(15);
        private readonly TimeSpan lockTime = TimeSpan.FromSeconds(30);
        private DocumentCollectionInfo feedCollectionLocation;
        private ChangeFeedProcessorOptions changeFeedProcessorOptions;
        private IChangeFeedDocumentClient feedDocumentClient;
        private FeedProcessing.IChangeFeedObserverFactory observerFactory;
        private string databaseResourceId;
        private string collectionResourceId;
        private DocumentCollectionInfo leaseCollectionLocation;
        private IChangeFeedDocumentClient leaseDocumentClient;
        private ILeaseManager leaseManager;
        private IParitionLoadBalancingStrategy loadBalancingStrategy;
        private IPartitionProcessorFactory partitionProcessorFactory;
        private IHealthMonitor healthMonitor;

        internal string HostName
        {
            get; private set;
        }

        /// <summary>
        /// Sets the Host name.
        /// </summary>
        /// <param name="hostName">Name to be used for the host. When using multiple hosts, each host must have a unique name.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithHostName(string hostName)
        {
            this.HostName = hostName;
            return this;
        }

        /// <summary>
        /// Sets the <see cref="DocumentCollectionInfo"/> of the collection to listen for changes.
        /// </summary>
        /// <param name="feedCollectionLocation"><see cref="DocumentCollectionInfo"/> of the collection to listen for changes</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithFeedCollection(DocumentCollectionInfo feedCollectionLocation)
        {
            if (feedCollectionLocation == null) throw new ArgumentNullException(nameof(feedCollectionLocation));
            this.feedCollectionLocation = feedCollectionLocation.Canonicalize();
            return this;
        }

        /// <summary>
        /// Sets an existing <see cref="DocumentClient"/> to be used to read from the monitored collection.
        /// </summary>
        /// <param name="feedDocumentClient">The instance of <see cref="DocumentClient"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithFeedDocumentClient(DocumentClient feedDocumentClient)
        {
            if (feedDocumentClient == null) throw new ArgumentNullException(nameof(feedDocumentClient));
            this.feedDocumentClient = new ChangeFeedDocumentClient(feedDocumentClient);
            return this;
        }

        /// <summary>
        /// Sets an existing <see cref="IChangeFeedDocumentClient"/> to be used to read from the monitored collection.
        /// </summary>
        /// <param name="feedDocumentClient">The instance of <see cref="IChangeFeedDocumentClient"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithFeedDocumentClient(IChangeFeedDocumentClient feedDocumentClient)
        {
            if (feedDocumentClient == null) throw new ArgumentNullException(nameof(feedDocumentClient));
            this.feedDocumentClient = feedDocumentClient;
            return this;
        }

        /// <summary>
        /// Sets the <see cref="ChangeFeedProcessorOptions"/> to be used by this instance of <see cref="IChangeFeedProcessor"/>.
        /// </summary>
        /// <param name="changeFeedProcessorOptions">The instance of <see cref="ChangeFeedProcessorOptions"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithProcessorOptions(ChangeFeedProcessorOptions changeFeedProcessorOptions)
        {
            if (changeFeedProcessorOptions == null) throw new ArgumentNullException(nameof(changeFeedProcessorOptions));
            this.changeFeedProcessorOptions = changeFeedProcessorOptions;
            return this;
        }

        /// <summary>
        /// Sets the <see cref="FeedProcessing.IChangeFeedObserverFactory"/> to be used to generate <see cref="IChangeFeedObserver"/>
        /// </summary>
        /// <param name="observerFactory">The instance of <see cref="FeedProcessing.IChangeFeedObserverFactory"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithObserverFactory(FeedProcessing.IChangeFeedObserverFactory observerFactory)
        {
            if (observerFactory == null) throw new ArgumentNullException(nameof(observerFactory));
            this.observerFactory = observerFactory;
            return this;
        }

        /// <summary>
        /// Sets an existing <see cref="IChangeFeedObserver"/> type to be used by a <see cref="FeedProcessing.IChangeFeedObserverFactory"/> to process changes.
        /// </summary>
        /// <typeparam name="T">Type of the <see cref="IChangeFeedObserver"/>.</typeparam>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithObserver<T>()
            where T : FeedProcessing.IChangeFeedObserver, new()
        {
            this.observerFactory = new ChangeFeedObserverFactory<T>();
            return this;
        }

        /// <summary>
        /// Sets the Database Resource Id of the monitored collection.
        /// </summary>
        /// <param name="databaseResourceId">Database Resource Id.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithDatabaseResourceId(string databaseResourceId)
        {
            if (databaseResourceId == null) throw new ArgumentNullException(nameof(databaseResourceId));
            this.databaseResourceId = databaseResourceId;
            return this;
        }

        /// <summary>
        /// Sets the Collection Resource Id of the monitored collection.
        /// </summary>
        /// <param name="collectionResourceId">Collection Resource Id.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithCollectionResourceId(string collectionResourceId)
        {
            if (collectionResourceId == null) throw new ArgumentNullException(nameof(collectionResourceId));
            this.collectionResourceId = collectionResourceId;
            return this;
        }

        /// <summary>
        /// Sets the <see cref="DocumentCollectionInfo"/> of the collection to use for leases.
        /// </summary>
        /// <param name="leaseCollectionLocation">The instance of a <see cref="DocumentCollectionInfo"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithLeaseCollection(DocumentCollectionInfo leaseCollectionLocation)
        {
            if (leaseCollectionLocation == null) throw new ArgumentNullException(nameof(leaseCollectionLocation));
            this.leaseCollectionLocation = leaseCollectionLocation.Canonicalize();
            return this;
        }

        /// <summary>
        /// Sets an existing <see cref="DocumentClient"/> to be used to read from the leases collection.
        /// </summary>
        /// <param name="leaseDocumentClient">The instance of <see cref="DocumentClient"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithLeaseDocumentClient(DocumentClient leaseDocumentClient)
        {
            if (leaseDocumentClient == null) throw new ArgumentNullException(nameof(leaseDocumentClient));
            this.leaseDocumentClient = new ChangeFeedDocumentClient(leaseDocumentClient);
            return this;
        }

        /// <summary>
        /// Sets an existing <see cref="IChangeFeedDocumentClient"/> to be used to read from the leases collection.
        /// </summary>
        /// <param name="leaseDocumentClient">The instance of <see cref="IChangeFeedDocumentClient"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithLeaseDocumentClient(IChangeFeedDocumentClient leaseDocumentClient)
        {
            if (leaseDocumentClient == null) throw new ArgumentNullException(nameof(leaseDocumentClient));
            this.leaseDocumentClient = leaseDocumentClient;
            return this;
        }

        /// <summary>
        /// Sets the <see cref="IParitionLoadBalancingStrategy"/> to be used for partition load balancing
        /// </summary>
        /// <param name="strategy">The instance of <see cref="IParitionLoadBalancingStrategy"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithPartitionLoadBalancingStrategy(IParitionLoadBalancingStrategy strategy)
        {
            if (strategy == null) throw new ArgumentNullException(nameof(strategy));
            this.loadBalancingStrategy = strategy;
            return this;
        }

        /// <summary>
        /// Sets the <see cref="IPartitionProcessorFactory"/> to be used to create <see cref="IPartitionProcessor"/> for partition processing.
        /// </summary>
        /// <param name="partitionProcessorFactory">The instance of <see cref="IPartitionProcessorFactory"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithPartitionProcessorFactory(IPartitionProcessorFactory partitionProcessorFactory)
        {
            if (partitionProcessorFactory == null) throw new ArgumentNullException(nameof(partitionProcessorFactory));
            this.partitionProcessorFactory = partitionProcessorFactory;
            return this;
        }

        /// <summary>
        /// Sets the <see cref="ILeaseManager"/> to be used to manage leases.
        /// </summary>
        /// <param name="leaseManager">The instance of <see cref="ILeaseManager"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithLeaseManager(ILeaseManager leaseManager)
        {
            if (leaseManager == null) throw new ArgumentNullException(nameof(leaseManager));
            this.leaseManager = leaseManager;
            return this;
        }

        /// <summary>
        /// Sets the <see cref="IHealthMonitor"/> to be used to monitor unhealthiness situation.
        /// </summary>
        /// <param name="healthMonitor">The instance of <see cref="IHealthMonitor"/> to use.</param>
        /// <returns>The instance of <see cref="ChangeFeedProcessorBuilder"/> to use.</returns>
        public ChangeFeedProcessorBuilder WithHealthMonitor(IHealthMonitor healthMonitor)
        {
            if (healthMonitor == null) throw new ArgumentNullException(nameof(healthMonitor));
            this.healthMonitor = healthMonitor;
            return this;
        }

        /// <summary>
        /// Builds a new instance of the <see cref="IChangeFeedProcessor"/> with the specified configuration.
        /// </summary>
        /// <returns>An instance of <see cref="IChangeFeedProcessor"/>.</returns>
        public async Task<IChangeFeedProcessor> BuildAsync()
        {
            if (this.HostName == null)
            {
                throw new InvalidOperationException("Host name was not specified");
            }

            if (this.feedCollectionLocation == null)
            {
                throw new InvalidOperationException(nameof(this.feedCollectionLocation) + " was not specified");
            }

            if (this.leaseCollectionLocation == null)
            {
                throw new InvalidOperationException(nameof(this.leaseCollectionLocation) + " was not specified");
            }

            if (this.observerFactory == null)
            {
                throw new InvalidOperationException("Observer was not specified");
            }

            await this.InitializeCollectionPropertiesForBuildAsync().ConfigureAwait(false);

            this.leaseDocumentClient = this.leaseDocumentClient ?? this.leaseCollectionLocation.CreateDocumentClient();
            var leaseCollectionMetadata = await GetCollectionMetadataAsync(
                this.leaseDocumentClient,
                this.leaseCollectionLocation,
                true).ConfigureAwait(false);
            ILeaseManager leaseManager = await this.GetLeaseManagerAsync(leaseCollectionMetadata).ConfigureAwait(false);

            IPartitionManager partitionManager = this.BuildPartitionManager(leaseManager, leaseCollectionMetadata);
            return new ChangeFeedProcessor(partitionManager);
        }

        /// <summary>
        /// Builds a new instance of the <see cref="IRemainingWorkEstimator"/> to estimate pending work with the specified configuration.
        /// </summary>
        /// <returns>An instance of <see cref="IRemainingWorkEstimator"/>.</returns>
        public async Task<IRemainingWorkEstimator> BuildEstimatorAsync()
        {
            if (this.feedCollectionLocation == null)
            {
                throw new InvalidOperationException(nameof(this.feedCollectionLocation) + " was not specified");
            }

            if (this.leaseCollectionLocation == null)
            {
                throw new InvalidOperationException(nameof(this.leaseCollectionLocation) + " was not specified");
            }

            await this.InitializeCollectionPropertiesForBuildAsync().ConfigureAwait(false);

            this.leaseDocumentClient = this.leaseDocumentClient ?? this.leaseCollectionLocation.CreateDocumentClient();
            var leaseCollectionMetadata = await GetCollectionMetadataAsync(
                this.leaseDocumentClient,
                this.leaseCollectionLocation,
                true).ConfigureAwait(false);
            ILeaseManager leaseManager = await this.GetLeaseManagerAsync(leaseCollectionMetadata).ConfigureAwait(false);

            IRemainingWorkEstimator remainingWorkEstimator = new RemainingWorkEstimator(
                leaseManager,
                this.feedDocumentClient,
                this.feedCollectionLocation.GetCollectionSelfLink(),
                this.feedCollectionLocation.ConnectionPolicy.MaxConnectionLimit);
            return remainingWorkEstimator;
        }

        private static async Task<string> GetDatabaseResourceIdAsync(IChangeFeedDocumentClient documentClient, DocumentCollectionInfo collectionLocation)
        {
            Logger.InfoFormat("Reading database: '{0}'", collectionLocation.DatabaseName);
            Uri databaseUri = UriFactory.CreateDatabaseUri(collectionLocation.DatabaseName);
            var response = await documentClient.ReadDatabaseAsync(databaseUri, null).ConfigureAwait(false);
            return response.Resource.ResourceId;
        }

        private static async Task<string> GetCollectionResourceIdAsync(IChangeFeedDocumentClient documentClient, DocumentCollectionInfo collectionLocation)
        {
            Logger.InfoFormat("Reading collection: '{0}'", collectionLocation.CollectionName);
            DocumentCollection documentCollection = await documentClient.GetDocumentCollectionAsync(collectionLocation).ConfigureAwait(false);
            return documentCollection.ResourceId;
        }

        private static async Task<CollectionMetadata> GetCollectionMetadataAsync(
            IChangeFeedDocumentClient documentClient,
            DocumentCollectionInfo collectionInfo,
            bool isPartitionKeyByIdRequiredIfPartitioned)
        {
            DocumentCollection collection = await documentClient.GetDocumentCollectionAsync(collectionInfo).ConfigureAwait(false);

            bool isPartitioned =
                collection.PartitionKey != null &&
                collection.PartitionKey.Paths != null &&
                collection.PartitionKey.Paths.Count > 0;
            if (isPartitioned && isPartitionKeyByIdRequiredIfPartitioned &&
                (collection.PartitionKey.Paths.Count != 1 || collection.PartitionKey.Paths[0] != "/id"))
            {
                throw new ArgumentException("The lease collection, if partitioned, must have partition key equal to id.");
            }

            var requestOptionsFactory = isPartitioned ?
                (IRequestOptionsFactory)new RequestOptionsFactoryForParitionedByIdCollection() :
                (IRequestOptionsFactory)new RequestOptionsFactoryForFixedCollection();

            return new CollectionMetadata(collection.SelfLink, isPartitioned, requestOptionsFactory);
        }

        private IPartitionManager BuildPartitionManager(ILeaseManager leaseManager, CollectionMetadata leaseCollectionMetadata)
        {
            this.leaseDocumentClient = this.leaseDocumentClient ?? this.leaseCollectionLocation.CreateDocumentClient();
            string feedCollectionSelfLink = this.feedCollectionLocation.GetCollectionSelfLink();
            var factory = new CheckpointerObserverFactory(this.observerFactory, this.changeFeedProcessorOptions.CheckpointFrequency);
            var synchronizer = new PartitionSynchronizer(this.feedDocumentClient, feedCollectionSelfLink, leaseManager, this.changeFeedProcessorOptions.DegreeOfParallelism, this.changeFeedProcessorOptions.QueryPartitionsMaxBatchSize);
            var leaseStore = new LeaseStore(
                this.leaseDocumentClient,
                this.leaseCollectionLocation,
                this.GetLeasePrefix(),
                leaseCollectionMetadata.SelfLink,
                leaseCollectionMetadata.RequestOptionsFactory);
            var bootstrapper = new Bootstrapper(synchronizer, leaseStore, this.lockTime, this.sleepTime);
            var partitionObserverFactory = new PartitionSupervisorFactory(
                factory,
                leaseManager,
                this.partitionProcessorFactory ?? new PartitionProcessorFactory(this.feedDocumentClient, this.changeFeedProcessorOptions, leaseManager, feedCollectionSelfLink),
                this.changeFeedProcessorOptions);

            if (this.loadBalancingStrategy == null)
            {
                this.loadBalancingStrategy = new EqualPartitionsBalancingStrategy(
                    this.HostName,
                    this.changeFeedProcessorOptions.MinPartitionCount,
                    this.changeFeedProcessorOptions.MaxPartitionCount,
                    this.changeFeedProcessorOptions.LeaseExpirationInterval);
            }

            IPartitionController partitionController = new PartitionController(leaseManager, partitionObserverFactory, synchronizer);

            if (this.healthMonitor == null)
            {
                this.healthMonitor = new TraceHealthMonitor();
            }

            partitionController = new HealthMonitoringPartitionControllerDecorator(partitionController, this.healthMonitor);
            var partitionLoadBalancer = new PartitionLoadBalancer(
                partitionController,
                leaseManager,
                this.loadBalancingStrategy,
                this.changeFeedProcessorOptions.LeaseAcquireInterval);
            return new PartitionManager(bootstrapper, partitionController, partitionLoadBalancer);
        }

        private async Task<ILeaseManager> GetLeaseManagerAsync(CollectionMetadata leaseCollectionMetadata)
        {
            if (this.leaseManager == null)
            {
                string leasePrefix = this.GetLeasePrefix();
                var leaseManagerBuilder = new LeaseManagerBuilder()
                    .WithLeasePrefix(leasePrefix)
                    .WithLeaseCollection(this.leaseCollectionLocation)
                    .WithLeaseCollectionLink(leaseCollectionMetadata.SelfLink)
                    .WithRequestOptionsFactory(leaseCollectionMetadata.RequestOptionsFactory)
                    .WithHostName(this.HostName);

                if (this.leaseDocumentClient != null)
                {
                    leaseManagerBuilder = leaseManagerBuilder.WithLeaseDocumentClient(this.leaseDocumentClient);
                }

                this.leaseManager = await leaseManagerBuilder.BuildAsync().ConfigureAwait(false);
            }

            return this.leaseManager;
        }

        private string GetLeasePrefix()
        {
            string optionsPrefix = this.changeFeedProcessorOptions.LeasePrefix ?? string.Empty;
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}_{2}_{3}", optionsPrefix, this.feedCollectionLocation.Uri.Host, this.databaseResourceId, this.collectionResourceId);
        }

        private async Task InitializeCollectionPropertiesForBuildAsync()
        {
            this.feedDocumentClient = this.feedDocumentClient ?? this.feedCollectionLocation.CreateDocumentClient();
            this.changeFeedProcessorOptions = this.changeFeedProcessorOptions ?? new ChangeFeedProcessorOptions();
            this.databaseResourceId = this.databaseResourceId ?? await GetDatabaseResourceIdAsync(this.feedDocumentClient, this.feedCollectionLocation).ConfigureAwait(false);
            this.collectionResourceId = this.collectionResourceId ?? await GetCollectionResourceIdAsync(this.feedDocumentClient, this.feedCollectionLocation).ConfigureAwait(false);
        }

        /// <summary>
        /// Internal metadata/attributes of the collection object.
        /// </summary>
        private class CollectionMetadata
        {
            public CollectionMetadata(string selfLink, bool isPatititioned, IRequestOptionsFactory requestOptionsFactory)
            {
                if (string.IsNullOrEmpty(selfLink))
                {
                    throw new ArgumentException(nameof(selfLink) + " cannot be null or empty.", nameof(selfLink));
                }

                if (requestOptionsFactory == null)
                {
                    throw new ArgumentException(nameof(requestOptionsFactory) + " cannot be null or empty.", nameof(requestOptionsFactory));
                }

                this.SelfLink = selfLink;
                this.IsPartitioned = isPatititioned;
                this.RequestOptionsFactory = requestOptionsFactory;
            }

            public string SelfLink { get; private set; }

            public bool IsPartitioned { get; private set; }

            public IRequestOptionsFactory RequestOptionsFactory { get; private set; }
        }
    }
}