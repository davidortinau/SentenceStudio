

using System.Collections;

namespace SentenceStudio.Common
{
    public static class ViewExtensions
    {
        public static T OnCheckChanged<T>(this T self, System.EventHandler<Microsoft.Maui.Controls.CheckedChangedEventArgs> handler) 
            where T : Microsoft.Maui.Controls.RadioButton
        {
            self.CheckedChanged += handler;
            return self;
        }

        public static T OnCheckChanged<T>(this T self, Action<T> action) 
            where T : Microsoft.Maui.Controls.RadioButton
        {
            self.CheckedChanged += (sender, e) => action?.Invoke(self);
            return self;
        }

        public static T OnClicked<T>(this T self, System.EventHandler handler)
            where T : class
        {
            var eventInfo = self.GetType().GetEvent("Clicked");
			if (eventInfo != null)
			{
				eventInfo.AddEventHandler(self, handler);
			}
			else
			{
				throw new InvalidOperationException($"The type {self.GetType().Name} does not have a Clicked event.");
			}
			return self;
        }

		public static T OnClicked<T>(this T self, System.Action<T> action)
            where T : Microsoft.Maui.Controls.Button
        {
            self.Clicked += (o, arg) => action(self);
            return self;
        }

		public static T OnTextChanged<T>(this T self, System.EventHandler<Microsoft.Maui.Controls.TextChangedEventArgs> handler) 
			where T : Microsoft.Maui.Controls.Entry
		{
			self.TextChanged += handler;
			return self;
		}

		public static T OnTextChanged<T>(this T self, Action<T> action) 
			where T : Microsoft.Maui.Controls.Entry
		{
			self.TextChanged += (sender, e) => action?.Invoke(self);
			return self;
		}

        public static T SelectedStateBackgroundColor<T>(this T visualElement, Color color) where T : VisualElement
		{
			if (VisualStateManager.GetVisualStateGroups(visualElement).FirstOrDefault(static x => x.Name is nameof(VisualStateManager.CommonStates)) is VisualStateGroup commonStatesGroup
				&& commonStatesGroup.States.FirstOrDefault(static x => x.Name is VisualStateManager.CommonStates.Selected) is VisualState selectedVisualState
				&& selectedVisualState.Setters.FirstOrDefault(static x => x.Property == VisualElement.BackgroundColorProperty) is Setter backgroundColorPropertySetter)
			{
				backgroundColorPropertySetter.Value = color;
			}
			else
			{
				VisualStateManager.SetVisualStateGroups(visualElement, new VisualStateGroupList
				{
					new VisualStateGroup
					{
						Name = nameof(VisualStateManager.CommonStates),
						States =
						{
							new VisualState
							{
								Name = VisualStateManager.CommonStates.Selected,
								Setters =
								{
									new Setter
									{
										Property= VisualElement.BackgroundColorProperty,
										Value = color
									}
								}
							}
						}
					}
				});
			}

            return visualElement;
		}
        
		public static TLayout AddMany<TLayout>(this TLayout layout, IEnumerable<IView> newViews) where TLayout : Layout
		{
			foreach (var view in newViews)
			{
				layout.Children.Add(view);
			}

			return layout;
		}

		public static TLayout PrependMany<TLayout>(this TLayout layout, IEnumerable<IView> newViews) where TLayout : Layout
		{
			var existingViews = layout.Children.ToList();
			layout.Children.Clear();

			foreach (var view in newViews)
			{
				layout.Children.Add(view);
			}

			foreach (var view in existingViews)
			{
				layout.Children.Add(view);
			}

			return layout;
		}

		public static T OnValueChanged<T>(this T self, System.EventHandler<ValueChangedEventArgs> handler)
            where T : Microsoft.Maui.Controls.Slider
        {
            self.ValueChanged += handler;
            return self;
        }

		public static T OnValueChanged<T>(this T self, Action<T> action) 
            where T : Microsoft.Maui.Controls.Slider
        {
            self.ValueChanged += (sender, e) => action?.Invoke(self);
            return self;
        }
    }
}