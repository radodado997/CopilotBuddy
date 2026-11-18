using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Styx.Helpers
{
	/// <summary>
	/// Base class for all settings. Ported from HB 3.3.5a Settings.cs.
	/// Per-character settings use flat path: Settings/FileName_{Name}.xml
	/// Supports migration from old paths via oldSettingsPath parameter.
	/// </summary>
	public abstract class Settings
	{
		[Browsable(false)]
		public string SettingsPath { get; private set; }

		/// <summary>
		/// Constructor matching HB 5.4.8 pattern.
		/// Supports migration from an old settings path to a new one.
		/// </summary>
		protected Settings(string settingsPath, string? oldSettingsPath = null)
			: this(settingsPath, oldSettingsPath, autoInitialize: true)
		{
		}

		/// <summary>
		/// Allows subclasses that own synchronized file creation/loading (e.g. under a
		/// cross-process mutex) to defer disk I/O until their own constructor acquires the lock.
		/// </summary>
		protected Settings(string settingsPath, string? oldSettingsPath, bool autoInitialize)
		{
			SettingsPath = settingsPath;
			InitializeDefaultValues();
			if (!autoInitialize)
				return;

			// Migration: if old path exists and new doesn't, move the file
			if (oldSettingsPath != null && File.Exists(oldSettingsPath) && !File.Exists(settingsPath))
			{
				string? dir = Path.GetDirectoryName(settingsPath);
				if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				{
					Directory.CreateDirectory(dir);
				}
				File.Move(oldSettingsPath, settingsPath);
			}

			if (!File.Exists(SettingsPath))
			{
				try { SaveToFile(SettingsPath); }
				catch (UnauthorizedAccessException ex)
				{
					Logging.Write("Could not create settings file {0}: {1} (using defaults)", SettingsPath, ex.Message);
				}
			}
			else
			{
				// A truncated/hand-edited XML file, or one written by another version,
				// used to throw straight out of the constructor and abort startup.
				// Keep the defaults and report the problem instead.
				try
				{
					LoadFromXML(XElement.Load(SettingsPath));
				}
				catch (Exception ex)
				{
					Logging.Write("Could not load settings from {0}: {1} (using defaults)", SettingsPath, ex.Message);
					Logging.WriteDebug("Settings load exception: {0}", ex);
				}
			}
		}

		/// <summary>
		/// Optional programmatic override for instance-isolated settings directory (primarily for tests).
		/// Defaults to null, which resolves <see cref="CommandLine.InstanceName"/>.
		/// </summary>
		public static string? InstanceNameOverride { get; set; }

		/// <summary>
		/// Effective sanitized instance name from <see cref="InstanceNameOverride"/> or <c>-instance</c>.
		/// </summary>
		public static string? ActiveInstanceName => SanitizeInstanceName(InstanceNameOverride ?? CommandLine.InstanceName);

		/// <summary>
		/// Computes the settings directory for a base application path and optional instance name.
		/// When <paramref name="instanceName"/> is non-empty, returns
		/// <c>{baseApplicationPath}/Settings/Instances/{instanceName}</c>; otherwise <c>{baseApplicationPath}/Settings</c>.
		/// </summary>
		public static string GetSettingsDirectory(string baseApplicationPath, string? instanceName)
		{
			string baseDir = Path.Combine(baseApplicationPath, "Settings");
			string? sanitized = SanitizeInstanceName(instanceName);
			if (!string.IsNullOrEmpty(sanitized))
			{
				return Path.Combine(baseDir, "Instances", sanitized);
			}
			return baseDir;
		}

		private static string? SanitizeInstanceName(string? instanceName)
		{
			if (string.IsNullOrWhiteSpace(instanceName))
				return null;

			char[] invalid = Path.GetInvalidFileNameChars();
			var sb = new StringBuilder(instanceName.Length);
			foreach (char c in instanceName.Trim())
			{
				if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar || Array.IndexOf(invalid, c) >= 0)
					sb.Append('_');
				else
					sb.Append(c);
			}

			string result = sb.ToString().Trim('.', ' ');
			return string.IsNullOrEmpty(result) ? null : result;
		}

		/// <summary>
		/// Root settings directory: {AppPath}/Settings/ or {AppPath}/Settings/Instances/{Instance}/
		/// when -instance is active.
		/// </summary>
		public static string SettingsDirectory
		{
			get
			{
				string dir = GetSettingsDirectory(Logging.ApplicationPath, ActiveInstanceName);
				if (!Directory.Exists(dir))
				{
					Directory.CreateDirectory(dir);
				}
				return dir;
			}
		}



		public Dictionary<string, object> GetSettings()
		{
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			Type type = this.GetType();
			foreach (PropertyInfo propertyInfo in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.GetProperty | BindingFlags.SetProperty))
			{
				object[] attributes = propertyInfo.GetCustomAttributes(typeof(SettingAttribute), false);
				if (attributes != null && attributes.Length > 0)
				{
					object value = propertyInfo.GetValue(this, null);
					string elementName = ((SettingAttribute)attributes[0]).ElementName ?? propertyInfo.Name;
					dictionary.Add(elementName, value);
				}
			}
			return dictionary;
		}

		public void InitializeDefaultValues()
		{
			Type type = this.GetType();
			foreach (PropertyInfo propertyInfo in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.GetProperty | BindingFlags.SetProperty))
			{
				object[] attributes = propertyInfo.GetCustomAttributes(typeof(DefaultValueAttribute), false);
				if (attributes != null && attributes.Length > 0)
				{
					DefaultValueAttribute defaultValueAttribute = (DefaultValueAttribute)attributes[0];
					try
					{
						propertyInfo.SetValue(this, defaultValueAttribute.Value, null);
					}
					catch (TargetException)
					{
						Logging.WriteDebug("Could not set property {0} to {1} (DefaultValueAttribute)",
							propertyInfo.Name,
							defaultValueAttribute.Value == null ? "null" : defaultValueAttribute.Value.ToString());
					}
				}
			}
		}

		public void SaveToFile(string path)
		{
			try
			{
				string directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
				{
					Directory.CreateDirectory(directory);
				}
				GetXML().Save(path);
			}
			catch (IOException ex)
			{
				Logging.Write("Error trying to save the settings file: " + ex.Message);
			}
		}

		public XElement GetXML()
		{
			Type type = this.GetType();
			PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.GetProperty | BindingFlags.SetProperty);
			XElement root = new XElement(System.Xml.XmlConvert.EncodeName(type.Name));

			foreach (PropertyInfo propertyInfo in properties)
			{
				// Check for [Setting] attribute BEFORE calling GetValue().
				// Pattern from HB 6.2.3. Prevents triggering lazy-loaded properties
				// (e.g. SingularSettings per-class wrappers) that lack [Setting].
				object[] attributes = propertyInfo.GetCustomAttributes(typeof(SettingAttribute), false);
				if (attributes == null || attributes.Length == 0)
					continue;

				object value = propertyInfo.GetValue(this, null);
				if (value != null)
				{
					SettingAttribute settingAttribute = (SettingAttribute)attributes[0];
					string elementName = settingAttribute.ElementName ?? propertyInfo.Name;

					if (!string.IsNullOrEmpty(settingAttribute.Explanation))
					{
						root.Add(new XComment(settingAttribute.Explanation));
					}

					XElement element;
					if (propertyInfo.PropertyType.IsArray)
					{
						element = new XElement(elementName);
						Array array = (Array)value;
						for (int i = 0; i < array.Length; i++)
						{
							object? item = array.GetValue(i);
							string? itemText = item is float or double or decimal
								? Convert.ToString(item, CultureInfo.InvariantCulture)
								: item?.ToString();
							element.Add(new XElement("Entry", itemText));
						}
					}
					else
					{
						string? valueText = value is float or double or decimal
							? Convert.ToString(value, CultureInfo.InvariantCulture)
							: value.ToString();
						element = new XElement(elementName, valueText);
					}
					root.Add(element);
				}
			}
			return root;
		}

		public void LoadFromXML(XElement root)
		{
			if (root == null)
			{
				throw new ArgumentNullException("root");
			}

			Type type = this.GetType();
			if (root.Name != System.Xml.XmlConvert.EncodeName(type.Name))
			{
				throw new ArgumentException(
					string.Format("The settings file does not match the type that is being loaded! Root name is \"{0}\", expected \"{1}\".",
						root.Name, type.Name),
					"root");
			}

			foreach (PropertyInfo propertyInfo in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.GetProperty | BindingFlags.SetProperty))
			{
				object[] attributes = propertyInfo.GetCustomAttributes(typeof(SettingAttribute), false);
				if (attributes != null && attributes.Length > 0)
				{
					SettingAttribute settingAttribute = (SettingAttribute)attributes[0];
					string elementName = settingAttribute.ElementName ?? propertyInfo.Name;
					XElement element = root.Element(elementName);

					if (element != null)
					{
						string textValue = element.Value;
						object convertedValue;

						if (propertyInfo.PropertyType.IsArray)
						{
							Type elementType = propertyInfo.PropertyType.GetElementType();
							XElement[] entries = element.Elements("Entry").ToArray();
							List<object> list = new List<object>();

							foreach (XElement entry in entries)
							{
								object obj = ConvertValue(entry.Value, elementType, "Entry in " + element.Name);
								if (obj != null)
								{
									list.Add(obj);
								}
							}

							Array array = Array.CreateInstance(elementType, list.Count);
							for (int i = 0; i < list.Count; i++)
							{
								array.SetValue(list[i], i);
							}
							convertedValue = array;
						}
						else
						{
							convertedValue = ConvertValue(textValue, propertyInfo.PropertyType, element.Name.ToString());
						}

						if (convertedValue == null && propertyInfo.PropertyType.IsValueType &&
						    Nullable.GetUnderlyingType(propertyInfo.PropertyType) == null)
						{
							// Conversion failed for a non-nullable value type: keep the
							// default instead of throwing and aborting the whole load.
							Logging.WriteDebug("Setting {0} could not be parsed; keeping the default value.", elementName);
							continue;
						}

						try
						{
							propertyInfo.SetValue(this, convertedValue, null);
						}
						catch (Exception ex)
						{
							Logging.WriteDebug("Setting {0} could not be assigned: {1}", elementName, ex.Message);
						}
					}
				}
			}
		}

		public void Save()
		{
			SaveToFile(SettingsPath);
		}

		/// <summary>
		/// Loads settings from the current settings file.
		/// Pattern from HB 5.4.8: checks File.Exists and uses StreamReader.
		/// </summary>
		public void Load()
		{
			if (File.Exists(SettingsPath))
			{
				using (TextReader reader = new StreamReader(SettingsPath, Encoding.UTF8, true))
				{
					LoadFromXML(XElement.Load(reader));
				}
			}
		}

		public static T LoadFromXML<T>(XElement root) where T : Settings, new()
		{
			T instance = new T();
			instance.LoadFromXML(root);
			return instance;
		}

		public static T LoadFromFile<T>(string path) where T : Settings, new()
		{
			if (!File.Exists(path))
			{
				throw new FileNotFoundException("Could not find the settings path!", path);
			}
			return LoadFromXML<T>(XElement.Load(path));
		}

		private static object ConvertValue(string val, Type t, string elementName)
		{
			try
			{
				if (t.IsEnum)
				{
					return Enum.Parse(t, val);
				}
				if (t == typeof(float))
				{
					if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float fInv))
						return fInv;
					return float.Parse(val, NumberStyles.Float, CultureInfo.CurrentCulture);
				}
				if (t == typeof(double))
				{
					if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double dInv))
						return dInv;
					return double.Parse(val, NumberStyles.Float, CultureInfo.CurrentCulture);
				}
				if (t == typeof(decimal))
				{
					if (decimal.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal mInv))
						return mInv;
					return decimal.Parse(val, NumberStyles.Float, CultureInfo.CurrentCulture);
				}
				return Convert.ChangeType(val, t);
			}
			catch (InvalidCastException)
			{
				Logging.Write("Setting {0} has invalid value \"{1}\"!", elementName, val);
			}
			catch (FormatException)
			{
				Logging.Write("Setting {0} has invalid format \"{1}\"!", elementName, val);
			}
			catch (ArgumentException)
			{
				// Enum.Parse throws ArgumentException for an unknown member name.
				Logging.Write("Setting {0} has an unknown value \"{1}\"!", elementName, val);
			}
			catch (OverflowException)
			{
				// Convert.ChangeType throws OverflowException when the number does not
				// fit the target type (e.g. 300 stored for a byte setting).
				Logging.Write("Setting {0} value \"{1}\" is out of range!", elementName, val);
			}
			catch (Exception ex)
			{
				Logging.Write("Setting {0} could not be converted from \"{1}\": {2}", elementName, val, ex.Message);
			}
			return null;
		}
	}
}
