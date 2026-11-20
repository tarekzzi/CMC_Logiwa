using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CMC_Logiwa
{
    /// <summary>
    /// Settings > Web VM / Logiwa (administrators only). The web VM sends the list of its settings (group, label, help, type, limits)
    /// with the values in force, so a setting added on the web VM shows up here without a new PC app. Only changed values are sent;
    /// the web VM checks everything again, stores the change with who / when and applies it without a restart.
    /// </summary>
    public partial class SettingsWindow
    {
        private sealed class WebField
        {
            public string Key;
            public string Label;
            public string Kind;
            public string Original;          // stored text format, as the web VM sent it
            public Func<JToken> Read;        // the editor's value in the format the web VM expects
            public TextBlock Error;
        }

        private readonly List<WebField> _webFields = new List<WebField>();
        private string _callerIp;

        private const string LogiwaKey = "Logiwa.Environment";
        private static readonly string[] IpKeys = { "Access.WebhookIps", "Access.PcIps" };

        private async void btnWebReload_Click(object sender, RoutedEventArgs e) => await LoadWebSettingsAsync();

        private async Task LoadWebSettingsAsync()
        {
            DbSettings s = DbSettingsStore.Load();
            panelWeb.Children.Clear();
            _webFields.Clear();

            if (s == null || !s.HasWebService)
            {
                panelWeb.Children.Add(Muted("Set the web service address and API key first (Database and web service tab)."));
                btnWebSave.IsEnabled = false;
                return;
            }

            panelWeb.Children.Add(Muted("Loading the web VM settings..."));
            btnWebReload.IsEnabled = btnWebSave.IsEnabled = false;
            try
            {
                JObject data;
                using (var web = new CmcWebClient(s))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    data = await web.GetSettingsAsync(cts.Token);
                ShowWebSettings(data);
                btnWebSave.IsEnabled = true;
            }
            catch (Exception ex)
            {
                panelWeb.Children.Clear();
                panelWeb.Children.Add(Danger("Cannot load the web VM settings: " + ex.Message));
            }
            finally
            {
                btnWebReload.IsEnabled = true;
            }
        }

        private void ShowWebSettings(JObject data)
        {
            panelWeb.Children.Clear();
            _webFields.Clear();

            var values = (JObject)data["values"] ?? new JObject();
            var fromPc = new HashSet<string>(((JArray)data["setFromPc"] ?? new JArray()).Select(t => (string)t), StringComparer.OrdinalIgnoreCase);
            _callerIp = data.Value<string>("callerIp");

            if (data.Value<bool?>("adminKeyConfigured") == false)
                panelWeb.Children.Add(Danger("No administrator key is set up on the web VM yet, so nothing can be saved here. Create one with DOC\\New-CmcApiKey.ps1 -Admin and put its hash in Key Vault (secret CmcApi--AdminKeyHashes--0)."));

            foreach (var group in ((JArray)data["definitions"] ?? new JArray()).OfType<JObject>().GroupBy(d => d.Value<string>("group")))
            {
                var card = new StackPanel();
                card.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("CardTitle") });

                foreach (JObject d in group)
                {
                    string key = d.Value<string>("key");
                    string current = values.Value<string>(key) ?? "";
                    var field = new WebField { Key = key, Label = d.Value<string>("label"), Kind = d.Value<string>("kind"), Original = current };
                    string help = d.Value<string>("help");
                    if (!fromPc.Contains(key)) help = (string.IsNullOrEmpty(help) ? "" : help + "  ") + "(Default or set on the web VM.)";

                    card.Children.Add(BuildEditor(d, field));
                    if (!string.IsNullOrEmpty(help)) card.Children.Add(Muted(help, 12));
                    field.Error = new TextBlock { Foreground = WebBrush("DangerBrush"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
                    card.Children.Add(field.Error);
                    card.Children.Add(new Border { Height = 12 });
                    _webFields.Add(field);
                }
                panelWeb.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = card });
            }

            // Read-only: start-up values and secrets stay on the web VM / in Key Vault.
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = "On the web VM only (read-only here)", Style = (Style)FindResource("CardTitle") });
            foreach (JObject i in (JArray)data["info"] ?? new JArray())
                info.Children.Add(Row(i.Value<string>("label"), i.Value<string>("value")));
            info.Children.Add(Row("This PC's address as the web VM sees it", _callerIp));
            panelWeb.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = info });

            var recent = new StackPanel();
            recent.Children.Add(new TextBlock { Text = "Recent changes", Style = (Style)FindResource("CardTitle") });
            var changes = ((JArray)data["recentChanges"] ?? new JArray()).OfType<JObject>().ToList();
            if (changes.Count == 0) recent.Children.Add(Muted("No changes made from a packing PC yet."));
            foreach (JObject c in changes)
            {
                DateTimeOffset.TryParse(c.Value<string>("changedAt"), out var at);
                recent.Children.Add(Muted($"{at.LocalDateTime:yyyy-MM-dd HH:mm}  {c.Value<string>("changedBy")}" +
                                          (string.IsNullOrEmpty(c.Value<string>("machineId")) ? "" : $" (machine {c.Value<string>("machineId")})") +
                                          $"  ·  {c.Value<string>("key")}: {Short(c.Value<string>("oldValue"))} → {Short(c.Value<string>("newValue"))}", 12.5));
            }
            panelWeb.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = recent });
        }

        private UIElement BuildEditor(JObject d, WebField f)
        {
            var panel = new StackPanel();
            string label = f.Label;

            switch (f.Kind)
            {
                case "Bool":
                {
                    var cb = new CheckBox { Content = label, IsChecked = f.Original == "true", Margin = new Thickness(0, 4, 0, 2) };
                    f.Read = () => cb.IsChecked == true;
                    panel.Children.Add(cb);
                    return panel;
                }
                case "Choice":
                {
                    panel.Children.Add(new Label { Content = label });
                    var choices = ((JArray)d["choices"] ?? new JArray()).Select(t => (string)t).ToList();
                    var combo = new ComboBox { MinHeight = 36, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
                    foreach (var c in choices) combo.Items.Add(c);
                    if (!choices.Contains(f.Original)) combo.Items.Insert(0, f.Original);   // e.g. "Other: <url>" set on the VM by hand
                    combo.SelectedItem = f.Original;
                    f.Read = () => (string)combo.SelectedItem ?? "";
                    panel.Children.Add(combo);
                    return panel;
                }
                case "List":
                case "IpList":
                {
                    panel.Children.Add(new Label { Content = label + "  (one per line)" });
                    var lines = string.Join(Environment.NewLine, ParseList(f.Original));
                    var tb = new TextBox { Text = lines, AcceptsReturn = true, MinHeight = 72, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    f.Read = () => new JArray(tb.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0));
                    panel.Children.Add(tb);
                    return panel;
                }
                case "Int":
                {
                    panel.Children.Add(new Label { Content = $"{label}  ({d.Value<int>("min")} to {d.Value<int>("max")})" });
                    var tb = new TextBox { Text = f.Original, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
                    f.Read = () => int.TryParse(tb.Text.Trim(), out int n) ? (JToken)n : tb.Text.Trim();
                    panel.Children.Add(tb);
                    return panel;
                }
                default:
                {
                    panel.Children.Add(new Label { Content = label });
                    var tb = new TextBox { Text = f.Original, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
                    f.Read = () => tb.Text.Trim();
                    panel.Children.Add(tb);
                    return panel;
                }
            }
        }

        /// <summary>The editor value in the stored text format, to compare with what the web VM sent.</summary>
        private static string AsStored(WebField f, JToken v)
        {
            switch (f.Kind)
            {
                case "Bool": return v.Value<bool>() ? "true" : "false";
                case "List":
                case "IpList":
                    return JsonConvert.SerializeObject(((JArray)v).Select(t => (string)t)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                default: return v.Type == JTokenType.Integer ? v.ToString() : (string)v;
            }
        }

        private async void btnWebSave_Click(object sender, RoutedEventArgs e)
        {
            if (!_isAdmin) return;
            foreach (var f in _webFields) f.Error.Visibility = Visibility.Collapsed;

            var changed = new JObject();
            foreach (var f in _webFields)
            {
                JToken v = f.Read();
                if (AsStored(f, v) != f.Original) changed[f.Key] = v;
            }
            if (changed.Count == 0)
            {
                ShowStatus("Nothing was changed.", error: false);
                return;
            }

            string adminKey = txtAdminKey.Password.Trim();
            if (adminKey.Length == 0)
            {
                ShowStatus("Type the administrator key to save web VM settings.", error: true);
                txtAdminKey.Focus();
                return;
            }

            // Changes that can stop the whole flow: say so before sending them.
            DbSettings s = DbSettingsStore.Load();
            if (changed[LogiwaKey] != null)
            {
                string choice = (string)changed[LogiwaKey];
                bool mismatch = (s?.EnvironmentName == DbSettings.Pro && choice == "Sandbox") || (s?.EnvironmentName == DbSettings.Dev && choice == "Production");
                string text = $"Switch the web VM to the Logiwa {choice} system?\n\nThe Logiwa login in Key Vault must belong to {choice}, otherwise every Logiwa call fails."
                              + (mismatch ? $"\n\nWARNING: this PC is set to {s.EnvironmentName}. {s.EnvironmentName} normally uses {(s.EnvironmentName == DbSettings.Pro ? "Production" : "Sandbox")}." : "");
                if (MessageBox.Show(this, text, "Logiwa system", MessageBoxButton.YesNo, mismatch ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }

            bool confirmOpen = false;
            foreach (var key in IpKeys)
            {
                if (changed[key] is JArray list && list.Count == 0)
                {
                    var f = _webFields.First(x => x.Key == key);
                    if (MessageBox.Show(this, $"\"{f.Label}\" will be empty: ANY address is let in (only the key / secret protects it).\n\nSave it anyway?",
                            "Empty address list", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                        return;
                    confirmOpen = true;
                }
            }
            if (changed["Access.WebhookIps"] != null &&
                MessageBox.Show(this, "A wrong webhook address list stops all new orders: Logiwa does not send a refused webhook again.\n\nSave the new list?",
                    "Webhook addresses", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            btnWebSave.IsEnabled = btnWebReload.IsEnabled = false;
            ShowStatus("Saving on the web VM...", error: false);
            try
            {
                CmcWebClient.SettingsSaveResult result;
                string machineId = _savedMachine?.MachineId;
                using (var web = new CmcWebClient(s))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    result = await web.SaveSettingsAsync(adminKey, SessionVariables.UserName, machineId, changed, confirmOpen, cts.Token);

                if (!result.Ok)
                {
                    var general = new List<string>();
                    foreach (var err in result.Errors)
                    {
                        var f = _webFields.FirstOrDefault(x => string.Equals(x.Key, err.Key, StringComparison.OrdinalIgnoreCase));
                        if (f == null) { general.Add(err.Value); continue; }
                        f.Error.Text = err.Value;
                        f.Error.Visibility = Visibility.Visible;
                        general.Add($"{f.Label}: {err.Value}");
                    }
                    ShowStatus("Not saved. " + string.Join("  ", general), error: true);
                    return;
                }

                txtAdminKey.Clear();
                ShowWebSettings(result.Settings);
                ShowStatus($"Saved {changed.Count} change(s) on the web VM. In force now.", error: false);
            }
            catch (Exception ex)
            {
                ShowStatus("Not saved: " + ex.Message, error: true);
            }
            finally
            {
                btnWebSave.IsEnabled = btnWebReload.IsEnabled = true;
            }
        }

        private static List<string> ParseList(string json)
        {
            try { return JsonConvert.DeserializeObject<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? new List<string>(); }
            catch (JsonException) { return new List<string>(); }
        }

        private static string Short(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "(none)";
            if (stored.StartsWith("[")) stored = string.Join(", ", ParseList(stored)) is var s && s.Length > 0 ? s : "(empty)";
            return stored.Length > 80 ? stored.Substring(0, 77) + "..." : stored;
        }

        private Brush WebBrush(string key) => (Brush)FindResource(key);

        private TextBlock Muted(string text, double size = 13) =>
            new TextBlock { Text = text, Style = (Style)FindResource("MutedText"), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

        private TextBlock Danger(string text) =>
            new TextBlock { Text = text, Foreground = WebBrush("DangerBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };

        private UIElement Row(string label, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = Muted(label);
            var v = new TextBlock { Text = value ?? "", TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            g.Children.Add(l);
            g.Children.Add(v);
            return g;
        }
    }
}
