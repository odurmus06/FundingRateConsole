namespace premiumIndexBot
{
    partial class main
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            dataGridView1 = new DataGridView();
            btn_refresh = new Button();
            lbl_interval = new Label();
            txt_interval = new TextBox();
            chk_interval = new CheckBox();
            timer_interval = new System.Windows.Forms.Timer(components);
            ((System.ComponentModel.ISupportInitialize)dataGridView1).BeginInit();
            SuspendLayout();
            // 
            // dataGridView1
            // 
            dataGridView1.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dataGridView1.Location = new Point(12, 12);
            dataGridView1.Name = "dataGridView1";
            dataGridView1.RowHeadersWidth = 51;
            dataGridView1.Size = new Size(925, 363);
            dataGridView1.TabIndex = 0;
            // 
            // btn_refresh
            // 
            btn_refresh.Location = new Point(12, 396);
            btn_refresh.Name = "btn_refresh";
            btn_refresh.Size = new Size(94, 29);
            btn_refresh.TabIndex = 1;
            btn_refresh.Text = "Re-Fresh";
            btn_refresh.UseVisualStyleBackColor = true;
            btn_refresh.Click += button1_Click;
            // 
            // lbl_interval
            // 
            lbl_interval.AutoSize = true;
            lbl_interval.Location = new Point(667, 400);
            lbl_interval.Name = "lbl_interval";
            lbl_interval.Size = new Size(55, 20);
            lbl_interval.TabIndex = 2;
            lbl_interval.Text = "Saniye:";
            // 
            // txt_interval
            // 
            txt_interval.Location = new Point(728, 397);
            txt_interval.Name = "txt_interval";
            txt_interval.Size = new Size(49, 27);
            txt_interval.TabIndex = 3;
            txt_interval.Text = "3";
            // 
            // chk_interval
            // 
            chk_interval.AutoSize = true;
            chk_interval.Location = new Point(783, 399);
            chk_interval.Name = "chk_interval";
            chk_interval.Size = new Size(154, 24);
            chk_interval.TabIndex = 4;
            chk_interval.Text = "Otomatik Güncelle";
            chk_interval.UseVisualStyleBackColor = true;
            chk_interval.CheckedChanged += chk_interval_CheckedChanged;
            // 
            // timer_interval
            // 
            timer_interval.Tick += timer_interval_Tick;
            // 
            // main
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(949, 450);
            Controls.Add(chk_interval);
            Controls.Add(txt_interval);
            Controls.Add(lbl_interval);
            Controls.Add(btn_refresh);
            Controls.Add(dataGridView1);
            Name = "main";
            Text = "main";
            ((System.ComponentModel.ISupportInitialize)dataGridView1).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private DataGridView dataGridView1;
        private Button btn_refresh;
        private Label lbl_interval;
        private TextBox txt_interval;
        private CheckBox chk_interval;
        private System.Windows.Forms.Timer timer_interval;
    }
}
