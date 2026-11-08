using RealGlass.Glass;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// Whose glass an asset window's rows show and change (<see cref="AssetCatalog"/>): the building
    /// picked in a city (<see cref="PickedAsset"/>, the building window) or the building being made in
    /// the asset editor (<see cref="EditedAsset"/>, the creator window).
    /// </summary>
    public abstract class AssetSource
    {
        /// <summary>There is an asset to show.</summary>
        public abstract bool HasSelection { get; }

        /// <summary>The rows can be moved (greyed out otherwise).</summary>
        public abstract bool CanEdit { get; }

        /// <summary>The settings this window gives the asset; null when it gives none.</summary>
        public abstract AssetRecord Own { get; }

        /// <summary>What the asset has beneath them, before the global settings: its creator's glass in a city; null for none.</summary>
        public virtual AssetRecord Beneath
        {
            get { return null; }
        }

        /// <summary>The global settings a row with nothing of the asset's own shows.</summary>
        public abstract GlassParams Global { get; }

        /// <summary>
        /// The shininess the asset's glass needs as read from its textures, where its glass follows it
        /// ("Detect per asset" on, a step from the slider); 0 for none. The creator window: none (the
        /// author's call, 2026-11-08: the detection is for cities).
        /// </summary>
        public virtual float DetectedThreshold
        {
            get { return 0f; }
        }

        /// <summary>The kind of glass those global settings are.</summary>
        public abstract int GlobalPreset { get; }

        /// <summary>The words over a row's reset icon (the language key).</summary>
        public abstract string ResetHintKey { get; }

        /// <summary>A copy of <see cref="Own"/> to change and <see cref="Store"/>.</summary>
        public abstract AssetRecord Editable();

        public abstract void Store(AssetRecord record, string what);
    }

    /// <summary>The building window's: the asset picked with the building picker, its glass in RealGlassAssets.xml.</summary>
    public sealed class PickedAsset : AssetSource
    {
        public override bool HasSelection
        {
            get { return AssetSelection.HasSelection; }
        }

        public override bool CanEdit
        {
            get { return (Settings.GlassEnabled == null || Settings.GlassEnabled.value) && AssetSelection.HasSelection; }
        }

        public override AssetRecord Own
        {
            get { return AssetSelection.Record; }
        }

        public override AssetRecord Beneath
        {
            get { return AssetSelection.CreatorRecord; }
        }

        public override GlassParams Global
        {
            get { return GlassSettings.Read(); }
        }

        public override float DetectedThreshold
        {
            get
            {
                if (!AssetSelection.HasSelection || AssetSelection.Info == null || !Settings.IsInitialised || !Settings.GlassThresholdAuto.value)
                    return 0f;
                float detected = GlassController.DetectedThresholdOf(AssetSelection.Info.name);
                return ThresholdDetect.Applies(detected, Settings.GlassThreshold.value) ? detected : 0f;
            }
        }

        public override int GlobalPreset
        {
            get { return Settings.IsInitialised ? Settings.GlassPreset.value : Settings.Defaults.GlassPreset; }
        }

        public override string ResetHintKey
        {
            get { return "Asset.Global.Hint"; }
        }

        public override AssetRecord Editable()
        {
            return AssetSelection.Editable();
        }

        public override void Store(AssetRecord record, string what)
        {
            AssetSelection.Store(record, what);
        }
    }

    /// <summary>The creator window's: the building being made in the asset editor, its glass in RealGlassDefinition.xml once saved.</summary>
    public sealed class EditedAsset : AssetSource
    {
        public override bool HasSelection
        {
            get { return CreatorAsset.Info != null; }
        }

        public override bool CanEdit
        {
            get { return CreatorAsset.Info != null && CreatorAsset.ShowGlass.value; }
        }

        public override AssetRecord Own
        {
            get { return CreatorAsset.Record; }
        }

        public override GlassParams Global
        {
            get { return GlassSettings.Read(); }
        }

        public override int GlobalPreset
        {
            get { return Settings.Defaults.GlassPreset; }
        }

        public override string ResetHintKey
        {
            get { return "Reset.Default"; }
        }

        public override AssetRecord Editable()
        {
            return CreatorAsset.Editable();
        }

        public override void Store(AssetRecord record, string what)
        {
            CreatorAsset.Store(record, what);
        }
    }
}
