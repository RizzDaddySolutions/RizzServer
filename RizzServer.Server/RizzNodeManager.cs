using Opc.Ua;
using Opc.Ua.Server;
using RizzServer.Core;

namespace RizzServer.Server;

/// <summary>Builds the address space (Objects/RizzServer/&lt;Connection&gt;/&lt;Tag&gt;) from the gateway's live tags.</summary>
public sealed class RizzNodeManager : CustomNodeManager2
{
    private readonly GatewayEngine _engine;
    private readonly Dictionary<TagRuntime, BaseDataVariableState> _nodes = new();

    public RizzNodeManager(IServerInternal server, ApplicationConfiguration configuration, GatewayEngine engine, string namespaceUri)
        : base(server, configuration, namespaceUri)
    {
        _engine = engine;
        SystemContext.NodeIdFactory = this;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
                externalReferences[ObjectIds.ObjectsFolder] = refs = new List<IReference>();

            var root = CreateFolder(null, "RizzServer", "RizzServer");
            root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            refs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));
            AddPredefinedNode(SystemContext, root);

            foreach (var group in _engine.Tags.GroupBy(t => t.Connection))
            {
                var folder = CreateFolder(root, group.Key.Name, group.Key.Name);
                foreach (var tag in group)
                    _nodes[tag] = CreateVariable(folder, tag);
            }
        }

        _engine.TagUpdated += OnTagUpdated;
    }

    public override void DeleteAddressSpace()
    {
        _engine.TagUpdated -= OnTagUpdated;
        base.DeleteAddressSpace();
    }

    private FolderState CreateFolder(NodeState? parent, string path, string name)
    {
        var folder = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId(path, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText("en", name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None,
        };
        parent?.AddChild(folder);
        return folder;
    }

    private BaseDataVariableState CreateVariable(NodeState parent, TagRuntime tag)
    {
        var writable = tag.Config.Access == TagAccess.ReadWrite;
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = tag.Config.Name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(tag.NodePath, NamespaceIndex),
            BrowseName = new QualifiedName(tag.Config.Name, NamespaceIndex),
            DisplayName = new LocalizedText("en", tag.Config.Name),
            DataType = MapDataType(tag.Config.DataType),
            ValueRank = ValueRanks.Scalar,
            AccessLevel = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            UserAccessLevel = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            Historizing = false,
            Value = null,
            StatusCode = StatusCodes.BadWaitingForInitialData,
            Timestamp = DateTime.UtcNow,
        };

        if (writable)
        {
            variable.OnSimpleWriteValue = (ISystemContext _, NodeState _, ref object value) =>
            {
                try
                {
                    _engine.WriteAsync(tag, value).Wait(TimeSpan.FromSeconds(5));
                    return ServiceResult.Good;
                }
                catch (Exception)
                {
                    return StatusCodes.BadCommunicationError;
                }
            };
        }

        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);
        return variable;
    }

    private void OnTagUpdated(TagRuntime tag)
    {
        if (!_nodes.TryGetValue(tag, out var node)) return;
        lock (Lock)
        {
            node.Value = tag.Value;
            node.StatusCode = tag.Quality switch
            {
                TagQuality.Good => StatusCodes.Good,
                TagQuality.Uncertain => StatusCodes.Uncertain,
                _ => tag.Value is null ? StatusCodes.BadWaitingForInitialData : StatusCodes.BadNoCommunication,
            };
            node.Timestamp = tag.Timestamp;
            node.ClearChangeMasks(SystemContext, false);
        }
    }

    private static NodeId MapDataType(TagDataType t) => t switch
    {
        TagDataType.Boolean => DataTypeIds.Boolean,
        TagDataType.Int16 => DataTypeIds.Int16,
        TagDataType.UInt16 => DataTypeIds.UInt16,
        TagDataType.Int32 => DataTypeIds.Int32,
        TagDataType.UInt32 => DataTypeIds.UInt32,
        TagDataType.Int64 => DataTypeIds.Int64,
        TagDataType.UInt64 => DataTypeIds.UInt64,
        TagDataType.Float => DataTypeIds.Float,
        TagDataType.Double => DataTypeIds.Double,
        _ => DataTypeIds.String,
    };
}
