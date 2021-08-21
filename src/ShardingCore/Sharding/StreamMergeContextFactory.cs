using ShardingCore.Core.VirtualRoutes.TableRoutes.RoutingRuleEngine;
using ShardingCore.Sharding.Abstractions;
using System.Linq;
using ShardingCore.Core.ShardingAccessors;

namespace ShardingCore.Sharding
{
    /*
    * @Author: xjm
    * @Description:
    * @Date: Thursday, 28 January 2021 16:52:43
    * @Email: 326308290@qq.com
    */
    public class StreamMergeContextFactory:IStreamMergeContextFactory
    {
        private readonly IRoutingRuleEngineFactory _routingRuleEngineFactory;
        private readonly IShardingScopeFactory _shardingScopeFactory;

        public StreamMergeContextFactory(
            IRoutingRuleEngineFactory routingRuleEngineFactory,IShardingScopeFactory shardingScopeFactory)
        {
            _routingRuleEngineFactory = routingRuleEngineFactory;
            _shardingScopeFactory = shardingScopeFactory;
        }
        public StreamMergeContext<T> Create<T>(IQueryable<T> queryable,IShardingDbContext shardingDbContext)
        {
            return new StreamMergeContext<T>(queryable,shardingDbContext, _routingRuleEngineFactory, _shardingScopeFactory);
        }
    }
}