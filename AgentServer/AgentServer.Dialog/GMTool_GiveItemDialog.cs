using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using LocalCommons.Logging;
using MySql.Data.MySqlClient;

namespace AgentServer.Dialog
{
	public class GMTool_GiveItemDialog : Form
	{
		private IContainer components;

		private Label label1;

		private Button button1;

		private TextBox textBox1;

		private TextBox textBox2;

		private Label label2;

		private TextBox textBox3;

		private Label label3;

		public GMTool_GiveItemDialog()
		{
			InitializeComponent();
			DarkUi.Apply(this, primaryButtons: true);
		}

		private void button1_Click(object sender, EventArgs e)
		{
			string nickname = (textBox1.Text ?? "").Trim();
			string itemText = (textBox2.Text ?? "").Trim();
			string countText = (textBox3.Text ?? "").Trim();
			if (nickname.Length == 0 || itemText.Length == 0 || countText.Length == 0)
			{
				DarkUi.Alert(this, "Cannot be empty", "OK");
				return;
			}
			int itemNum;
			int giveCount;
			if (!int.TryParse(itemText, out itemNum) || !int.TryParse(countText, out giveCount) || giveCount < 1)
			{
				DarkUi.Alert(this, "Item number or count is not a number", "OK");
				return;
			}
			try
			{
				using MySqlConnection mySqlConnection = new MySqlConnection(Conf.Connstr);
				mySqlConnection.Open();
				int userNum;
				using (MySqlCommand find = new MySqlCommand("SELECT fdUserNum FROM userinfo WHERE fdNickname=@n LIMIT 1", mySqlConnection))
				{
					find.Parameters.AddWithValue("@n", nickname);
					object found = find.ExecuteScalar();
					if (found == null || found == DBNull.Value)
					{
						DarkUi.Alert(this, "Nickname not found", "OK");
						return;
					}
					userNum = Convert.ToInt32(found);
				}
				int character = 0;
				using (MySqlCommand item = new MySqlCommand("SELECT fdCharacter FROM tblavataritemdesc WHERE fdItemNum=@n LIMIT 1", mySqlConnection))
				{
					item.Parameters.AddWithValue("@n", itemNum);
					object found = item.ExecuteScalar();
					if (found != null && found != DBNull.Value)
					{
						character = Convert.ToInt32(found);
					}
				}
				using (MySqlCommand give = new MySqlCommand(
					"INSERT INTO tblavataruser (fdUserNum, fdItemDescNum, fdCharacter, fdCount, fdUsing, fdGotDateTime, fdExpireDateTime) " +
					"VALUES (@u,@n,@ch,@c,0,NOW(),DATE_ADD(NOW(), INTERVAL 30 DAY)) " +
					"ON DUPLICATE KEY UPDATE fdCount=fdCount+@c, fdExpireDateTime=DATE_ADD(NOW(), INTERVAL 30 DAY)", mySqlConnection))
				{
					give.Parameters.AddWithValue("@u", userNum);
					give.Parameters.AddWithValue("@n", itemNum);
					give.Parameters.AddWithValue("@ch", character);
					give.Parameters.AddWithValue("@c", giveCount);
					give.ExecuteNonQuery();
				}
				using (MySqlCommand give = new MySqlCommand(
					"INSERT INTO tblavatarusercharacter (fdUserNum, fdItemDescNum, fdCharacter, fdCount, fdUsing, fdGotDateTime, fdExpireDateTime) " +
					"VALUES (@u,@n,@ch,@c,0,NOW(),DATE_ADD(NOW(), INTERVAL 30 DAY)) " +
					"ON DUPLICATE KEY UPDATE fdCount=fdCount+@c, fdExpireDateTime=DATE_ADD(NOW(), INTERVAL 30 DAY)", mySqlConnection))
				{
					give.Parameters.AddWithValue("@u", userNum);
					give.Parameters.AddWithValue("@n", itemNum);
					give.Parameters.AddWithValue("@ch", character);
					give.Parameters.AddWithValue("@c", giveCount);
					give.ExecuteNonQuery();
				}
				Log.Info("give item {0} x{1} to {2} character {3}", itemNum, giveCount, nickname, character);
				DarkUi.Alert(this, "Successfully", "OK");
			}
			catch (Exception ex)
			{
				Log.Error("give item {0} to {1}: {2}", itemNum, nickname, ex.Message);
				DarkUi.Alert(this, ex.Message, "OK");
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && components != null)
			{
				components.Dispose();
			}
			base.Dispose(disposing);
		}

		private void InitializeComponent()
		{
			this.label1 = new System.Windows.Forms.Label();
			this.button1 = new System.Windows.Forms.Button();
			this.textBox1 = new System.Windows.Forms.TextBox();
			this.textBox2 = new System.Windows.Forms.TextBox();
			this.label2 = new System.Windows.Forms.Label();
			this.textBox3 = new System.Windows.Forms.TextBox();
			this.label3 = new System.Windows.Forms.Label();
			base.SuspendLayout();
			this.label1.AutoSize = true;
			this.label1.Location = new System.Drawing.Point(19, 17);
			this.label1.Name = "label1";
			this.label1.Size = new System.Drawing.Size(71, 16);
			this.label1.TabIndex = 0;
			this.label1.Text = "NickName:";
			this.button1.Location = new System.Drawing.Point(67, 138);
			this.button1.Name = "button1";
			this.button1.Size = new System.Drawing.Size(115, 34);
			this.button1.TabIndex = 1;
			this.button1.Text = "OK";
			this.button1.UseVisualStyleBackColor = true;
			this.button1.Click += new System.EventHandler(button1_Click);
			this.textBox1.Location = new System.Drawing.Point(96, 14);
			this.textBox1.MaxLength = 30;
			this.textBox1.Name = "textBox1";
			this.textBox1.Size = new System.Drawing.Size(126, 23);
			this.textBox1.TabIndex = 2;
			this.textBox2.Location = new System.Drawing.Point(96, 53);
			this.textBox2.MaxLength = 6;
			this.textBox2.Name = "textBox2";
			this.textBox2.Size = new System.Drawing.Size(126, 23);
			this.textBox2.TabIndex = 4;
			this.label2.AutoSize = true;
			this.label2.Location = new System.Drawing.Point(26, 56);
			this.label2.Name = "label2";
			this.label2.Size = new System.Drawing.Size(64, 16);
			this.label2.TabIndex = 3;
			this.label2.Text = "ItemNum:";
			this.textBox3.Location = new System.Drawing.Point(96, 93);
			this.textBox3.MaxLength = 1;
			this.textBox3.Name = "textBox3";
			this.textBox3.Size = new System.Drawing.Size(126, 23);
			this.textBox3.TabIndex = 6;
			this.textBox3.Text = "1";
			this.label3.AutoSize = true;
			this.label3.Location = new System.Drawing.Point(17, 96);
			this.label3.Name = "label3";
			this.label3.Size = new System.Drawing.Size(73, 16);
			this.label3.TabIndex = 5;
			this.label3.Text = "Give Count:";
			base.AutoScaleDimensions = new System.Drawing.SizeF(7f, 16f);
			base.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			base.ClientSize = new System.Drawing.Size(247, 189);
			base.Controls.Add(this.textBox3);
			base.Controls.Add(this.label3);
			base.Controls.Add(this.textBox2);
			base.Controls.Add(this.label2);
			base.Controls.Add(this.textBox1);
			base.Controls.Add(this.button1);
			base.Controls.Add(this.label1);
			this.Font = new System.Drawing.Font("Microsoft Sans Serif", 9f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, 0);
			base.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
			base.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
			base.MaximizeBox = false;
			base.Name = "GMTool_GiveItemDialog";
			this.Text = "Give Item";
			base.ResumeLayout(false);
			base.PerformLayout();
		}
	}
}
