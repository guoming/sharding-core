﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShardingCore.Core.EntityMetadatas;
using ShardingCore.Core.PhysicTables;
using ShardingCore.Core.TrackerManagers;
using ShardingCore.Core.VirtualDatabase;
using ShardingCore.Core.VirtualDatabase.VirtualDataSources;
using ShardingCore.Core.VirtualDatabase.VirtualDataSources.PhysicDataSources;
using ShardingCore.Core.VirtualDatabase.VirtualTables;
using ShardingCore.Core.VirtualRoutes.DataSourceRoutes;
using ShardingCore.Core.VirtualRoutes.TableRoutes;
using ShardingCore.Core.VirtualRoutes.TableRoutes.RouteTails.Abstractions;
using ShardingCore.Core.VirtualTables;
using ShardingCore.Extensions;
using ShardingCore.Jobs;
using ShardingCore.Jobs.Abstaractions;
using ShardingCore.Sharding.Abstractions;
using ShardingCore.TableCreator;
using ShardingCore.Utils;

namespace ShardingCore.Bootstrapers
{
    /*
    * @Author: xjm
    * @Description:
    * @Date: 2021/9/20 14:04:55
    * @Ver: 1.0
    * @Email: 326308290@qq.com
    */
    public interface IShardingDbContextBootstrapper
    {
        void Init();
    }

    public class ShardingDbContextBootstrapper<TShardingDbContext> : IShardingDbContextBootstrapper where TShardingDbContext : DbContext, IShardingDbContext
    {
        private readonly IShardingConfigOption _shardingConfigOption;
        private readonly IRouteTailFactory _routeTailFactory;
        private readonly IVirtualTableManager<TShardingDbContext> _virtualTableManager;
        private readonly IShardingTableCreator<TShardingDbContext> _tableCreator;
        private readonly ILogger<ShardingDbContextBootstrapper<TShardingDbContext>> _logger;

        public ShardingDbContextBootstrapper(IShardingConfigOption shardingConfigOption)
        {
            _shardingConfigOption = shardingConfigOption;
            _routeTailFactory = ShardingContainer.GetService<IRouteTailFactory>();
            _virtualTableManager = ShardingContainer.GetService<IVirtualTableManager<TShardingDbContext>>();
            _tableCreator = ShardingContainer.GetService<IShardingTableCreator<TShardingDbContext>>();
            _logger = ShardingContainer.GetService<ILogger<ShardingDbContextBootstrapper<TShardingDbContext>>>();
        }
        public void Init()
        {
            var virtualDataSource = ShardingContainer.GetService<IVirtualDataSource<TShardingDbContext>>();
            var dataSources = _shardingConfigOption.GetDataSources();
            virtualDataSource.AddPhysicDataSource(new DefaultPhysicDataSource(_shardingConfigOption.DefaultDataSourceName, _shardingConfigOption.DefaultConnectionString, true));
            //foreach (var dataSourceKv in dataSources)
            //{
            //    virtualDataSource.AddPhysicDataSource(new DefaultPhysicDataSource(dataSourceKv.Key,
            //        dataSourceKv.Value, false));
            //}
            ISet<Type> entitiesInitSet = new HashSet<Type>();
            foreach (var dataSourceKv in dataSources)
            {

                using (var serviceScope = ShardingContainer.Services.CreateScope())
                {
                    var dataSourceName = dataSourceKv.Key;
                    var connectionString = dataSourceKv.Value;
                    virtualDataSource.AddPhysicDataSource(new DefaultPhysicDataSource(dataSourceName, connectionString, false));
                    using var context =
                        (DbContext)serviceScope.ServiceProvider.GetService(_shardingConfigOption.ShardingDbContextType);
                    if (_shardingConfigOption.EnsureCreatedWithOutShardingTable)
                        EnsureCreated(context, dataSourceName);
                    foreach (var entity in context.Model.GetEntityTypes())
                    {
                        var entityType = entity.ClrType;

                        if (_shardingConfigOption.HasVirtualDataSourceRoute(entityType) ||
                        _shardingConfigOption.HasVirtualTableRoute(entityType))
                        {
                            //只初始化一次
                            if (!entitiesInitSet.Contains(entityType))
                            {
                                //获取ShardingEntity的实际表名
#if !EFCORE2
                                var virtualTableName = context.Model.FindEntityType(entityType).GetTableName();
#endif
#if EFCORE2
                                var virtualTableName = context.Model.FindEntityType(entityType).Relational().TableName;
#endif
                                var entityMetadataInitializerType = typeof(EntityMetadataInitializer<,>).GetGenericType1(typeof(TShardingDbContext), entityType);
                                var constructors
                                    = entityMetadataInitializerType.GetTypeInfo().DeclaredConstructors
                                        .Where(c => !c.IsStatic && c.IsPublic)
                                        .ToArray();
                                var @params = constructors[0].GetParameters().Select((o, i) =>
                                {


                                    if (i == 0)
                                    {
                                        if (o.ParameterType != typeof(EntityMetadataEnsureParams))
                                            throw new InvalidOperationException($"{typeof(EntityMetadataInitializer<,>).FullName} constructors first params type should {typeof(EntityMetadataEnsureParams).FullName}");
                                        return new EntityMetadataEnsureParams(dataSourceName, entity, virtualTableName);
                                    }

                                    return ShardingContainer.GetService(o.ParameterType);
                                }).ToArray();
                                var entityMetadataInitializer = (IEntityMetadataInitializer)Activator.CreateInstance(entityMetadataInitializerType, @params);
                                entityMetadataInitializer.Initialize();
                                entitiesInitSet.Add(entityType);
                            }

                            var virtualTable = _virtualTableManager.GetVirtualTable(entityType);
                            //创建表
                            CreateDataTable(dataSourceName, virtualTable);
                            //var virtualTableRoute = virtualTable.GetVirtualRoute();
                            ////添加任务
                            //if (virtualTableRoute is IJob routeJob && routeJob.StartJob())
                            //{
                            //    var jobManager = ShardingContainer.GetService<IJobManager>();
                            //    var jobEntries = JobTypeParser.Parse(virtualTableRoute.GetType());
                            //    jobEntries.ForEach(o =>
                            //    {
                            //        o.JobName = $"{routeJob.JobName}:{o.JobName}";
                            //    });
                            //    foreach (var jobEntry in jobEntries)
                            //    {
                            //        jobManager.AddJob(jobEntry);
                            //    }
                            //}
                        }
                    }
                }
            }
        }
        private void CreateDataTable(string dataSourceName, IVirtualTable virtualTable)
        {
            var entityMetadata = virtualTable.EntityMetadata;
            foreach (var tail in virtualTable.GetVirtualRoute().GetAllTails())
            {
                if (NeedCreateTable(entityMetadata))
                {
                    try
                    {
                        //添加物理表
                        virtualTable.AddPhysicTable(new DefaultPhysicTable(virtualTable, tail));
                        _tableCreator.CreateTable(dataSourceName, entityMetadata.EntityType, tail);
                    }
                    catch (Exception e)
                    {
                        if (!_shardingConfigOption.IgnoreCreateTableError.GetValueOrDefault())
                        {
                            _logger.LogWarning(
                                $"table :{virtualTable.GetVirtualTableName()}{entityMetadata.TableSeparator}{tail} will created.", e);
                        }
                    }
                }
                else
                {
                    //添加物理表
                    virtualTable.AddPhysicTable(new DefaultPhysicTable(virtualTable, tail));
                }

            }
        }
        private bool NeedCreateTable(EntityMetadata entityMetadata)
        {
            if (entityMetadata.AutoCreateTable.HasValue)
            {
                if (entityMetadata.AutoCreateTable.Value)
                    return entityMetadata.AutoCreateTable.Value;
                else
                {
                    if (entityMetadata.AutoCreateDataSourceTable.HasValue)
                        return entityMetadata.AutoCreateDataSourceTable.Value;
                }
            }
            if (entityMetadata.AutoCreateDataSourceTable.HasValue)
            {
                if (entityMetadata.AutoCreateDataSourceTable.Value)
                    return entityMetadata.AutoCreateDataSourceTable.Value;
                else
                {
                    if (entityMetadata.AutoCreateTable.HasValue)
                        return entityMetadata.AutoCreateTable.Value;
                }
            }

            return _shardingConfigOption.CreateShardingTableOnStart.GetValueOrDefault();
        }
        private void EnsureCreated(DbContext context, string dataSourceName)
        {
            if (context is IShardingDbContext shardingDbContext)
            {
                var dbContext = shardingDbContext.GetDbContext(dataSourceName, false, _routeTailFactory.Create(string.Empty));
                var modelCacheSyncObject = dbContext.GetModelCacheSyncObject();

                lock (modelCacheSyncObject)
                {
                    var shardingEntitiyTypes = _shardingConfigOption.GetShardingTableRouteTypes();
                    dbContext.RemoveDbContextRelationModelThatIsShardingTable(shardingEntitiyTypes);
                    dbContext.Database.EnsureCreated();
                    dbContext.RemoveModelCache();
                }
            }
        }




    }
}
