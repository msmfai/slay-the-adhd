extends SceneTree
func _initialize():
	var pck := OS.get_environment("PCK_OUT")
	var ok := ProjectSettings.load_resource_pack(pck)
	print("load_resource_pack ok: ", ok)
	print("res://mod_manifest.json exists: ", ResourceLoader.exists("res://mod_manifest.json"))
	if FileAccess.file_exists("res://mod_manifest.json"):
		print("contents: ", FileAccess.get_file_as_string("res://mod_manifest.json").substr(0,80))
	quit(0)
