

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
            where T : Microsoft.Maui.Controls.Button
        {
            self.Clicked += handler;
            return self;
        }
        
        public static T OnClicked<T>(this T self, System.Action<T> action)
            where T : Microsoft.Maui.Controls.Button
        {
            self.Clicked += (o, arg) => action(self);
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
    }
}