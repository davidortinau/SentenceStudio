using The49.Maui.BottomSheet;

namespace SentenceStudio.Pages.Scene;

public partial class ImageGalleryBottomSheet : BottomSheet
{
	public ImageGalleryBottomSheet()
	{
		InitializeComponent();
		BindingContext = this;
	}

    public ImageGalleryBottomSheet(DescribeAScenePageModel model)
    {
		InitializeComponent();
		BindingContext = model;
    }

	
}