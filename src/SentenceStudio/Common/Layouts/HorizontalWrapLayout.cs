using Microsoft.Maui.Layouts;

namespace CustomLayouts
{
    public class HorizontalWrapLayout : Microsoft.Maui.Controls.StackLayout
    {
        public HorizontalWrapLayout()
        {
        }

        protected override ILayoutManager CreateLayoutManager()
        {
            return new HorizontalWrapLayoutManager(this);
        }
    }

    
}

