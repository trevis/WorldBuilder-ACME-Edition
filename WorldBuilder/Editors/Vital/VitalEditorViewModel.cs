using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Vital {
    public partial class VitalEditorViewModel : ViewModelBase {
        private IDatReaderWriter? _dats;
        private Project? _project;
        private VitalTable? _vitalTable;

        [ObservableProperty] private string _statusText = "No vital table loaded";

        [ObservableProperty] private SkillFormulaViewModel? _healthFormula;
        [ObservableProperty] private SkillFormulaViewModel? _staminaFormula;
        [ObservableProperty] private SkillFormulaViewModel? _manaFormula;

        public WorldBuilderSettings Settings { get; }

        public VitalEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            LoadVitalTable();
        }

        private void LoadVitalTable() {
            if (_dats == null) return;

            if (!_dats.TryGet<VitalTable>(0x0E000003, out var table)) {
                StatusText = "Failed to load VitalTable from DAT";
                return;
            }

            _vitalTable = table;
            HealthFormula = new SkillFormulaViewModel("Health", table.Health);
            StaminaFormula = new SkillFormulaViewModel("Stamina", table.Stamina);
            ManaFormula = new SkillFormulaViewModel("Mana", table.Mana);

            StatusText = "Loaded VitalTable (0x0E000003)";
        }

        [RelayCommand]
        private void Save() {
            if (_vitalTable == null || _dats == null) return;

            HealthFormula?.ApplyTo(_vitalTable.Health);
            StaminaFormula?.ApplyTo(_vitalTable.Stamina);
            ManaFormula?.ApplyTo(_vitalTable.Mana);

            if (!_dats.TrySave(_vitalTable)) {
                StatusText = "Failed to save VitalTable";
                return;
            }

            StatusText = "Saved VitalTable successfully";
        }
    }

    public partial class SkillFormulaViewModel : ObservableObject {
        public string VitalName { get; }

        [ObservableProperty] private int _unknown;
        [ObservableProperty] private bool _hasSecondAttribute;
        [ObservableProperty] private bool _useFormula;
        [ObservableProperty] private int _divisor;
        [ObservableProperty] private AttributeId _attribute1;
        [ObservableProperty] private AttributeId _attribute2;

        public IReadOnlyList<AttributeId> AllAttributes { get; } = Enum.GetValues<AttributeId>();

        public string FormulaDisplay => HasSecondAttribute
            ? $"({Attribute1} + {Attribute2}) / {Divisor}"
            : $"{Attribute1} / {Divisor}";

        partial void OnHasSecondAttributeChanged(bool value) => OnPropertyChanged(nameof(FormulaDisplay));
        partial void OnAttribute1Changed(AttributeId value) => OnPropertyChanged(nameof(FormulaDisplay));
        partial void OnAttribute2Changed(AttributeId value) => OnPropertyChanged(nameof(FormulaDisplay));
        partial void OnDivisorChanged(int value) => OnPropertyChanged(nameof(FormulaDisplay));

        public SkillFormulaViewModel(string vitalName, SkillFormula formula) {
            VitalName = vitalName;
            Divisor = formula.Divisor;
            Attribute1 = formula.Attribute1;
            Attribute2 = formula.Attribute2;
            UseFormula = formula.Attribute1Multiplier > 0;
            HasSecondAttribute = formula.Attribute2Multiplier > 0;
            Unknown = formula.AdditiveBonus;
        }

        public void ApplyTo(SkillFormula formula) {
            formula.Divisor = Divisor;
            formula.Attribute1 = Attribute1;
            formula.Attribute2 = Attribute2;
            formula.Attribute1Multiplier = UseFormula ? 1 : 0;
            formula.Attribute2Multiplier = HasSecondAttribute ? 1 : 0;
            formula.AdditiveBonus = Unknown;
        }
    }
}
