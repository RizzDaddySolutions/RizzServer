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

            // Objects/RizzServer/<Channel>/<Device>/<Tag>; NodeIds are "Channel", "Channel.Device", "Channel.Device.Tag".
            foreach (var channel in _engine.Tags.GroupBy(t => t.ChannelName))
            {
                var channelFolder = CreateFolder(root, channel.Key, channel.Key);
                foreach (var device in channel.GroupBy(t => t.DeviceName))
                {
                    var deviceFolder = CreateFolder(channelFolder, $"{channel.Key}.{device.Key}", device.Key);
                    foreach (var tag in device)
                        _nodes[tag] = CreateVariable(deviceFolder, tag);
                }
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
        if (parent is not null)
        {
            parent.AddChild(folder);
            AddPredefinedNode(SystemContext, folder); // register so the folder is browsable
        }
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

        if (!string.IsNullOrWhiteSpace(tag.Config.Description))
            variable.Description = new LocalizedText("en", tag.Config.Description);

        if (writable)
        {
            variable.OnSimpleWriteValue = (ISystemContext context, NodeState _, ref object value) =>
            {
                // Anonymous sessions and Viewer accounts read; Operator and Admin may write.
                var who = WhoIs(context);
                if (who.Role < UserRole.Operator)
                {
                    _engine.Audit?.Invoke("write-denied", who.Name, tag.NodePath, $"{value}");
                    return StatusCodes.BadUserAccessDenied;
                }
                try
                {
                    _engine.WriteAsync(tag, value).Wait(TimeSpan.FromSeconds(5));
                    _engine.Audit?.Invoke("write", who.Name, tag.NodePath, $"{value}");
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

        if (tag.Config.Units is { Length: > 0 } units)
            AddProperty(variable, tag, BrowseNames.EngineeringUnits, DataTypeIds.EUInformation, new EUInformation(units, units, ""));
        if (tag.Config.EuMin is not null && tag.Config.EuMax is not null)
            AddProperty(variable, tag, BrowseNames.EURange, DataTypeIds.Range, new Opc.Ua.Range(tag.Config.EuMax.Value, tag.Config.EuMin.Value));
        return variable;
    }

    private void AddProperty<T>(BaseDataVariableState owner, TagRuntime tag, string name, NodeId dataType, T value)
    {
        var property = new PropertyState<T>(owner)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypeIds.HasProperty,
            TypeDefinitionId = VariableTypeIds.PropertyType,
            NodeId = new NodeId($"{tag.NodePath}.{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, 0),
            DisplayName = new LocalizedText("en", name),
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        owner.AddChild(property);
        AddPredefinedNode(SystemContext, property);
    }

    /// <summary>The account behind a session: a named user, or "anonymous" (read-only).</summary>
    private (string Name, UserRole Role) WhoIs(ISystemContext context)
    {
        var identity = (context as ServerSystemContext)?.OperationContext?.UserIdentity;
        if (identity?.TokenType != UserTokenType.UserName) return ("anonymous", UserRole.Viewer);
        var user = _engine.Config.Server.Users.FirstOrDefault(u => string.Equals(u.Username, identity.DisplayName, StringComparison.Ordinal));
        return (identity.DisplayName ?? "?", user?.Role ?? UserRole.Viewer);
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
