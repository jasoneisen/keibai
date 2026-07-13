/**
 * <p>
 * TOP画面avaScript
 * </p>
 * 
 * @version 1.0
 * @date 2020-11-05 新規作成
 * @author Hitachi Social Information Services, Ltd.
 * 
 */
    /**
     * <p>
     * TOP画面の競売物件情報リンク押下
     * </p>
     * 
     */
	function tranPropertyResult(prefecturesId,courtId,saleScdId,saleCls,tabId) {
		$("input[name='prefecturesId']").val(prefecturesId);
		$("input[name='courtId']").val(courtId);
		$("input[name='saleScdId']").val(saleScdId);
		$("input[name='saleCls']").val(saleCls);
		$("input[name='tabId']").val(tabId);
		$("#topForm").submit();
	}
	
	/**
     * <p>
     * TOP画面の売却結果情報リンク押下
     * </p>
     * 
     */
	function tranResult(prefecturesId,courtId,saleScdId,saleCls,tabId) {
		$("input[name='prefecturesId']").val(prefecturesId);
		$("input[name='courtId']").val(courtId);
		$("input[name='saleScdId']").val(saleScdId);
		$("input[name='saleCls']").val(saleCls);
		$("input[name='tabId']").val(tabId);
		$("#topForm").submit();
	}
	
		/**
     * <p>
     * TOP画面全国地図の地域選択押下
     * </p>
     * 
     */
	function tranAreaMap(blockCls,tabId) {
		$("input[name='blockCls']").val(blockCls);
		$("input[name='tabId']").val(tabId);
		$("#topForm").submit();
	}

 