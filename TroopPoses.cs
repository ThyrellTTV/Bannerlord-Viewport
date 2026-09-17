using System.IO;
using System.Numerics;
using System.Xml.Linq;
using System.Globalization;
using TpacTool.Lib;

namespace LOTRAOM_Viewport;

internal sealed class TroopPoses
{
    private readonly SkeletonDefinitionData _skeleton;
    private readonly int[] _parents;
    private readonly Matrix4x4[] _rest;
    private readonly Matrix4x4[] _inverseBind;
    private readonly Matrix4x4[] _bind;
    private static readonly Matrix4x4 ToViewport = new(
        1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    private static readonly Matrix4x4 FromViewport = Matrix4x4.Transpose(ToViewport);

    internal int BoneCount => _rest.Length;
    internal IReadOnlyList<PoseAnimation> Animations { get; }
    private readonly Dictionary<string, PoseAnimation> _holdingPoses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _holsters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Matrix4x4> _holsterRoots = new(StringComparer.Ordinal);

    internal TroopPoses(string nativeAssetFolder)
    {
        var skeletonPackage = new AssetPackage(Path.Combine(nativeAssetFolder, "core_game.tpac"),
            assetTypes: new HashSet<Guid> { Skeleton.TYPE_GUID });
        var skeleton = skeletonPackage.Items.OfType<Skeleton>().Single(asset => asset.Name == "human_skeleton");
        _skeleton = skeleton.Definition?.Data ?? throw new InvalidDataException("Human bind skeleton is missing.");
        _parents = _skeleton.CreateParentLookup();
        _rest = _skeleton.Bones.Select(bone =>
        {
            var matrix = bone.RestFrame;
            // TPAC stores flags in the padding of these affine 4x3 bone frames.
            matrix.M14 = matrix.M24 = matrix.M34 = 0;
            matrix.M44 = 1;
            return matrix;
        }).ToArray();
        _inverseBind = new Matrix4x4[BoneCount];
        _bind = new Matrix4x4[BoneCount];
        for (var i = 0; i < BoneCount; i++)
        {
            if (_parents[i] >= i) throw new InvalidDataException("Bind skeleton is not in parent-first order.");
            _bind[i] = _parents[i] < 0 ? _rest[i] : _rest[i] * _bind[_parents[i]];
            if (!Matrix4x4.Invert(_bind[i], out _inverseBind[i])) throw new InvalidDataException("Invalid bind bone matrix.");
        }
        var animations = new AssetPackage(Path.Combine(nativeAssetFolder, "animations.tpac"),
            assetTypes: new HashSet<Guid> { SkeletalAnimation.TYPE_GUID });
        var options = animations.Items.OfType<SkeletalAnimation>()
            .Where(asset => asset.Skeleton == skeleton.Guid && asset.BoneNum == BoneCount && asset.Definition != null && asset.Duration > 0)
            .Select(asset => new PoseAnimation(asset.Name.StartsWith("anim_", StringComparison.Ordinal) ? asset.Name[5..] : asset.Name, asset))
            .OrderBy(animation => animation.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        var clipPath = Path.Combine(nativeAssetFolder, "animation_clips.tpac");
        if (File.Exists(clipPath))
        {
            var clips = new AssetPackage(clipPath, assetTypes: new HashSet<Guid> { AnimationClip.TYPE_GUID });
            foreach (var (name, display) in new[] {
                ("stand_1h", "One-handed holding pose"), ("stand_1h_with_shield", "Weapon and shield holding pose"),
                ("stand_2h", "Two-handed sword holding pose"), ("stand_2h_axe", "Two-handed axe holding pose"),
                ("stand_staff", "Polearm holding pose"), ("stand_staff_shield", "Polearm and shield holding pose"),
                ("stand_bow", "Bow holding pose"), ("stand_crossbow", "Crossbow holding pose"),
                ("stand_thrown", "Throwing weapon holding pose"), ("stand_non_combat", "Sheathed equipment pose") })
                if (HoldingPose(name, display) is { } pose) _holdingPoses[name] = pose;

            PoseAnimation? HoldingPose(string clipName, string displayName)
            {
                var clip = clips.Items.OfType<AnimationClip>().FirstOrDefault(clip => clip.Name == clipName);
                var source = options.FirstOrDefault(animation => animation.Source!.Guid == clip?.Animation)?.Source;
                return source == null || clip == null ? null : new PoseAnimation(displayName, source, clip.Source1, clip.Source2);
            }
        }
        var holsterPath = Path.Combine(nativeAssetFolder, "..", "ModuleData", "item_holsters.xml");
        if (File.Exists(holsterPath))
            foreach (var holster in XDocument.Load(holsterPath).Descendants("item_holster"))
                if ((string?)holster.Attribute("id") is { } id) _holsters[id] = holster;
        var holsterRigPath = Path.Combine(nativeAssetFolder, "skeletons.tpac");
        if (File.Exists(holsterRigPath))
            foreach (var rig in new AssetPackage(holsterRigPath, assetTypes: new HashSet<Guid> { Skeleton.TYPE_GUID })
                .Items.OfType<Skeleton>().Where(rig => rig.Name.StartsWith("holster_", StringComparison.Ordinal)))
                if (rig.Definition?.Data.Bones.FirstOrDefault() is { } root)
                {
                    var rest = root.RestFrame;
                    rest.M14 = rest.M24 = rest.M34 = 0;
                    rest.M44 = 1;
                    _holsterRoots[rig.Name] = rest;
                }
        Animations = options.Concat(_holdingPoses.Values)
            .OrderBy(animation => animation.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal PoseClip Load(PoseAnimation animation)
    {
        var definition = animation.Source?.Definition?.Data ?? throw new InvalidDataException("Animation tracks are missing.");
        if (definition.BoneAnims.Count != BoneCount) throw new InvalidDataException("Animation skeleton does not match the equipment rig.");
        var frames = definition.BoneAnims.SelectMany(bone => bone.RotationFrames.Keys.Concat(bone.PositionFrames.Keys))
            .Concat(definition.RootPositionFrames.Keys).Concat(definition.RootScaleFrames.Keys).ToArray();
        if (frames.Length == 0) throw new InvalidDataException("Animation has no readable keyframes.");
        var first = frames.Min();
        var last = frames.Max();
        // Bannerlord reserves source frame zero for the reference pose.
        if (first == 0 && last >= 1) first = 1;
        if (animation.FirstFrame is { } start) first = Math.Clamp(start, first, last);
        if (animation.LastFrame is { } end) last = Math.Clamp(end, first, last);
        return new PoseClip(definition, first, last);
    }

    internal PoseAnimation? GetHoldingPose(string category, bool shield)
    {
        var name = category switch {
            "TwoHandedSword" => "stand_2h",
            "TwoHandedAxe" or "TwoHandedMace" => "stand_2h_axe",
            "OneHandedPolearm" or "TwoHandedPolearm" or "LowGripPolearm" => shield ? "stand_staff_shield" : "stand_staff",
            "Bow" => "stand_bow",
            "Crossbow" or "Musket" => "stand_crossbow",
            "Javelin" or "ThrowingAxe" or "ThrowingKnife" or "Stone" => "stand_thrown",
            "" when !shield => "stand_non_combat",
            _ => shield ? "stand_1h_with_shield" : "stand_1h" };
        return _holdingPoses.GetValueOrDefault(name);
    }

    internal (int Bone, Matrix4x4 Frame, string Group) GetHolsterAttachment(string[] candidates, Vector3 shift, ISet<string> occupied)
    {
        foreach (var name in candidates)
        {
            if (!_holsters.TryGetValue(name, out var holster)) continue;
            var group = (string?)holster.Attribute("group_name") ?? name;
            if (occupied.Contains(group)) continue;
            var path = new HashSet<string>(StringComparer.Ordinal);
            var (boneName, frame) = Resolve(name);
            var bone = _skeleton.Bones.FindIndex(node => node.Name == boneName);
            if (bone < 0) throw new InvalidDataException($"Holster attachment bone '{boneName}' is missing.");
            var skeletonName = (string?)holster.Attribute("holster_skeleton");
            if (skeletonName == null || !_holsterRoots.TryGetValue(skeletonName, out var holsterRoot))
                throw new InvalidDataException($"Authored holster skeleton '{skeletonName}' is missing.");
            frame = holsterRoot * Matrix4x4.CreateTranslation(shift) * frame * _bind[bone];
            return (bone, FromViewport * frame * ToViewport, group);

            (string Bone, Matrix4x4 Frame) Resolve(string id)
            {
                if (!path.Add(id)) throw new InvalidDataException("Cyclic holster base set.");
                var node = _holsters[id];
                var baseId = (string?)node.Attribute("base_set");
                var parent = baseId == null ? ("spine2", Matrix4x4.Identity) : Resolve(baseId);
                var boneId = (string?)node.Attribute("holster_bone");
                var boneName = boneId switch { "biped_thorax" => "spine2", "biped_abdomen" => "spine",
                    "biped_item_r" => "r_finger0", "biped_item_l" => "l_finger0", "biped_forearm1_l" => "l_foretwist1",
                    null => parent.Item1, _ => throw new InvalidDataException($"Unsupported holster bone '{boneId}'.") };
                var angles = ParseVector((string?)node.Attribute("holster_rotation_yaw_pitch_roll")) * (MathF.PI / 180);
                // Holsters use yaw/pitch/roll (Z/Y/X), not weapon XML's XYZ Euler order.
                var local = Matrix4x4.CreateRotationX(angles.Z) * Matrix4x4.CreateRotationY(angles.Y) * Matrix4x4.CreateRotationZ(angles.X);
                local.Translation = ParseVector((string?)node.Attribute("holster_position"));
                return (boneName, local * parent.Item2);
            }
        }
        throw new InvalidDataException("No available authored holster position for this item.");

        static Vector3 ParseVector(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Vector3.Zero;
            var parts = value.Split(',').Select(part => float.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            if (parts.Length != 3 || parts.Any(part => !float.IsFinite(part))) throw new InvalidDataException("Invalid holster vector.");
            return new Vector3(parts[0], parts[1], parts[2]);
        }
    }

    internal (int Bone, Matrix4x4 Frame) GetItemAttachment(string boneName, Matrix4x4 itemFrame)
    {
        var bone = _skeleton.Bones.FindIndex(node => node.Name == boneName);
        if (bone < 0) throw new InvalidDataException($"Equipment attachment bone '{boneName}' is missing.");
        return (bone, FromViewport * itemFrame * _bind[bone] * ToViewport);
    }

    internal Matrix4x4[] Evaluate(PoseClip clip, float frame)
    {
        frame = Math.Clamp(frame, clip.FirstFrame, clip.LastFrame);
        var definition = clip.Definition;
        var global = new Matrix4x4[BoneCount];
        var matrices = new Matrix4x4[BoneCount];
        for (var i = 0; i < BoneCount; i++)
        {
            var track = definition.BoneAnims[i];
            var local = _rest[i];
            if (track.RotationFrames.Count > 0)
            {
                local = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(track.GetInterpolatedRotation(TrackFrame(track.RotationFrames, frame), out _, out _)));
                local.Translation = _rest[i].Translation;
            }
            if (track.PositionFrames.Count > 0)
            {
                var position = track.GetInterpolatedPosition(TrackFrame(track.PositionFrames, frame), out _, out _);
                local.Translation = new Vector3(position.X, position.Y, position.Z);
            }
            if (_parents[i] < 0)
            {
                if (definition.RootPositionFrames.Count > 0)
                {
                    var root = definition.GetInterpolatedPosition(TrackFrame(definition.RootPositionFrames, frame), out _, out _);
                    // Keep locomotion in place, while retaining vertical motion for crouches/jumps.
                    local.Translation += new Vector3(0, 0, root.Z);
                }
                if (definition.RootScaleFrames.Count > 0)
                    local = Matrix4x4.CreateScale(definition.GetInterpolatedScale(TrackFrame(definition.RootScaleFrames, frame), out _, out _)) * local;
            }
            global[i] = _parents[i] < 0 ? local : local * global[_parents[i]];
            matrices[i] = FromViewport * _inverseBind[i] * global[i] * ToViewport;
        }
        return matrices;
    }

    private static float TrackFrame<T>(SortedList<float, AnimationFrame<T>> frames, float frame) where T : struct
        => Math.Clamp(frame, frames.Keys[0], frames.Keys[^1]);
}

internal sealed record PoseAnimation(string DisplayName, SkeletalAnimation? Source, float? FirstFrame = null, float? LastFrame = null)
{
    internal static readonly PoseAnimation BindPose = new("Bind pose", null);
}

internal sealed record PoseClip(AnimationDefinitionData Definition, float FirstFrame, float LastFrame);
