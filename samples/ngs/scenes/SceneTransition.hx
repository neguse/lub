package scenes;

enum SceneTransition {
	Stay;
	Switch(s:Scene);
	Quit;
}
