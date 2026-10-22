using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace RandomEverywhereInstaller.App.ViewModels;

public sealed partial class CompanionViewModel
{
    // Only deferred settings are tracked. Recorded master volume/mute stay live.
    private static readonly string[] DraftNames =
    [
        nameof(SyntheticVolume), nameof(RecordedDevice), nameof(RecordedPack), nameof(HeadsetDevice),
        nameof(ExternalDevice), nameof(VoiceLanguage), nameof(TextPack), nameof(AudioInhibit),
        nameof(MissionVolumes), nameof(HeadsetLevel), nameof(ExternalLevel), nameof(NotifyLevel),
        nameof(RadioQuality), nameof(RadioBeep), nameof(RadioNoise), nameof(BeepLevel), nameof(RadioTail),
        nameof(Variety), nameof(CustomCallSign), nameof(CallSign)
    ];
    private static readonly PropertyInfo[] DraftProperties = DraftNames.Select(name => typeof(CompanionViewModel).GetProperty(name)!).ToArray();
    private readonly List<INotifyPropertyChanged> _draftRows = [];
    private string _acceptedAudioFingerprint = string.Empty;
    private Action? _restoreAudioDraft;
    private bool _hasPendingAudioEdits;
    public bool HasPendingAudioEdits
    {
        get => _hasPendingAudioEdits;
        private set
        {
            if (!SetProperty(ref _hasPendingAudioEdits, value)) return;
            OnPropertyChanged(nameof(AudioEditsStatus));
            RefreshCommands();
        }
    }
    public string AudioEditsStatus => T(HasPendingAudioEdits ? "C.Audio.Pending" : "C.Audio.Applied");

    private void InitializeAudioDraftTracking()
    {
        PropertyChanged += (_, args) =>
        {
            if (DraftNames.Contains(args.PropertyName)) RefreshAudioDraftState();
        };
        Actors.CollectionChanged += (_, _) => { if (!_loading) { WireAudioDraftRows(); RefreshAudioDraftState(); } };
        RecordedSounds.CollectionChanged += (_, _) => { if (!_loading) { WireAudioDraftRows(); RefreshAudioDraftState(); } };
        CaptureAudioDraft();
    }

    private void WireAudioDraftRows()
    {
        foreach (var row in _draftRows) row.PropertyChanged -= AudioDraftRowChanged;
        _draftRows.Clear();
        _draftRows.AddRange(Actors);
        _draftRows.AddRange(RecordedSounds);
        foreach (var row in _draftRows) row.PropertyChanged += AudioDraftRowChanged;
    }

    private void AudioDraftRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is "Voice" or "Speed" or "Random" or "Pool" or "Enabled") RefreshAudioDraftState();
    }

    private string AudioFingerprint() => JsonSerializer.Serialize(new
    {
        Values = DraftProperties.ToDictionary(property => property.Name, property =>
            property.GetValue(this) is CompanionChoice choice ? choice.Id : property.GetValue(this)),
        Actors = Actors.Select(row => new { row.Actor, Voice = row.Voice?.Id, row.Speed, row.Random, row.Pool }),
        Sounds = RecordedSounds.Select(row => new { row.Id, row.Variable, row.Enabled })
    });

    private void RefreshAudioDraftState()
    {
        if (!_loading && _restoreAudioDraft is not null)
            HasPendingAudioEdits = AudioFingerprint() != _acceptedAudioFingerprint;
    }

    private void CaptureAudioDraft()
    {
        var values = DraftProperties.Select(property => (Property: property, Value: property.GetValue(this))).ToArray();
        var actors = Actors.Select(row => (Row: row, row.Voice, row.Speed, row.Random, Pool: row.Pool.ToArray())).ToArray();
        var sounds = RecordedSounds.Select(row => (Row: row, row.Enabled)).ToArray();
        var selectedSound = RecordedSound;
        _restoreAudioDraft = () =>
        {
            _loading = true;
            try
            {
                foreach (var value in values) value.Property.SetValue(this, value.Value);
                Actors.Clear();
                foreach (var actor in actors)
                {
                    actor.Row.Voice = actor.Voice; actor.Row.Speed = actor.Speed;
                    actor.Row.Random = actor.Random; actor.Row.Pool = actor.Pool.ToArray();
                    Actors.Add(actor.Row);
                }
                RecordedSounds.Clear();
                foreach (var sound in sounds) { sound.Row.Enabled = sound.Enabled; RecordedSounds.Add(sound.Row); }
                RecordedSound = selectedSound;
            }
            finally { _loading = false; }
            WireAudioDraftRows();
            RefreshAudioDraftState();
        };
        WireAudioDraftRows();
        _acceptedAudioFingerprint = AudioFingerprint();
        HasPendingAudioEdits = false;
    }

    public bool TryLeaveAudioEdits()
    {
        if (!HasPendingAudioEdits) return true;
        if (!_dialogs.Confirm(AppEdition.ProductName, T("C.Audio.DiscardConfirm"))) return false;
        _restoreAudioDraft?.Invoke();
        return true;
    }
}
