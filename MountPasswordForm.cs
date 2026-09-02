using System.Security.Cryptography;
using System.Text;

namespace VaultLatch;

internal sealed class MountPasswordForm : Form
{
    private const int MaxPasswordBytes = 128;
    private const int MaxPimValue = 2_147_468;

    private readonly TextBox _passwordBox;
    private readonly NumericUpDown _pimInput;
    private readonly CheckBox _showPassword;
    private byte[]? _passwordUtf8;

    public int Pim => Decimal.ToInt32(_pimInput.Value);

    public MountPasswordForm(string volumePath, string driveLetter)
    {
        Text = "解锁 VaultLatch";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(470, 245);

        var containerName = Path.GetFileName(volumePath);
        Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(22, 18),
            Size = new Size(425, 42),
            Text = $"VaultLatch 将直接通过 VeraCrypt 驱动挂载到 {driveLetter}:。\r\n容器：{containerName}",
        });

        Controls.Add(new Label { AutoSize = true, Location = new Point(22, 76), Text = "密码" });
        _passwordBox = new TextBox
        {
            Location = new Point(102, 72),
            Size = new Size(340, 27),
            UseSystemPasswordChar = true,
            MaxLength = 128,
        };
        Controls.Add(_passwordBox);

        _showPassword = new CheckBox
        {
            AutoSize = true,
            Location = new Point(102, 105),
            Text = "显示密码",
        };
        _showPassword.CheckedChanged += (_, _) => _passwordBox.UseSystemPasswordChar = !_showPassword.Checked;
        Controls.Add(_showPassword);

        Controls.Add(new Label { AutoSize = true, Location = new Point(22, 143), Text = "PIM" });
        _pimInput = new NumericUpDown
        {
            Location = new Point(102, 139),
            Size = new Size(145, 27),
            Minimum = 0,
            Maximum = MaxPimValue,
            Value = 0,
            ThousandsSeparator = false,
        };
        Controls.Add(_pimInput);
        Controls.Add(new Label
        {
            AutoSize = true,
            Location = new Point(258, 143),
            Text = "默认 0；未设置自定义 PIM 时保持 0",
        });

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(352, 196),
            Size = new Size(90, 30),
        };
        Controls.Add(cancelButton);

        var mountButton = new Button
        {
            Text = "解锁",
            Location = new Point(252, 196),
            Size = new Size(90, 30),
        };
        mountButton.Click += (_, _) => AcceptPassword();
        Controls.Add(mountButton);

        AcceptButton = mountButton;
        CancelButton = cancelButton;
        Shown += (_, _) => _passwordBox.Focus();
    }

    public byte[] TakePasswordUtf8()
    {
        if (_passwordUtf8 is null)
        {
            throw new InvalidOperationException("密码尚未确认。");
        }

        var result = _passwordUtf8;
        _passwordUtf8 = null;
        return result;
    }

    private void AcceptPassword()
    {
        byte[] passwordBytes;
        try
        {
            passwordBytes = Encoding.UTF8.GetBytes(_passwordBox.Text);
        }
        catch
        {
            MessageBox.Show(this, "密码无法转换为 UTF-8。", "VaultLatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (passwordBytes.Length is < 1 or > MaxPasswordBytes)
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            MessageBox.Show(
                this,
                $"VeraCrypt 密码必须为 1-{MaxPasswordBytes} 个 UTF-8 字节。",
                "VaultLatch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _passwordUtf8 = passwordBytes;
        _passwordBox.Clear();
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _passwordBox.Clear();
            if (_passwordUtf8 is not null)
            {
                CryptographicOperations.ZeroMemory(_passwordUtf8);
                _passwordUtf8 = null;
            }
        }

        base.Dispose(disposing);
    }
}
