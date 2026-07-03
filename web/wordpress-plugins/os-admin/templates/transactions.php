<?php if (!defined('ABSPATH')) exit; ?>

<script type="text/javascript">
document.addEventListener("DOMContentLoaded", function() {
    const toggleZeroTransactions = document.getElementById('toggleZeroTransactions');
    toggleZeroTransactions.addEventListener('change', function() {
        const zeroAmountRows = document.querySelectorAll('.zero-amount');
        zeroAmountRows.forEach(row => {
            row.style.display = this.checked ? 'none' : '';
        });
    });
});
</script>

<?php if (!empty($transactions)): ?>

<style>
    <style>
    .table-xp {
    width: 100%;
    border-collapse: collapse;
    font-family: Tahoma, sans-serif;
    font-size: 12px;
}

.table-xp th, .table-xp td {
    border: 1px solid #A5A5A5;
    padding: 4px 8px;
    text-align: left;
}

.table-xp th {
    background: linear-gradient(to bottom, #D4D0C8, #B6B2AA);
    color: black;
    font-weight: bold;
    border-bottom: 2px solid #808080;
}

.table-xp tr:nth-child(even) {
    background-color: #E8E8E8;
}

.table-xp tr:hover {
    background-color: #C3D9FF;
}

.table-xp input {
    border: 1px inset #808080;
    padding: 2px;
    background-color: white;
    font-size: 12px;
    font-family: Tahoma, sans-serif;
}

.table-xp select {
    border: 1px solid #808080;
    padding: 2px;
    background-color: white;
    font-size: 12px;
    font-family: Tahoma, sans-serif;
}
</style>

<p>
    <input type="checkbox" id="toggleZeroTransactions">
    <label for="toggleZeroTransactions">Hide 0 transactions</label>
</p>

<table class="table-xp">
    <thead>
        <tr>
            <th>Date</th>
            <th>Sender</th>
            <th>Receiver</th>
            <th>Amount</th>
            <th>Description</th>  
        </tr>
    </thead>
    <tbody>
        <?php foreach ($transactions as $tx): 
            $sender = ($tx->sender === '00000000-0000-0000-0000-000000000000') ? '--' : esc_html($tx->sender);
            $receiver = ($tx->receiver === '00000000-0000-0000-0000-000000000000') ? '--' : esc_html($tx->receiver);
            $zeroAmountClass = ($tx->amount == 0) ? 'zero-amount' : '';
            $formattedAmount = number_format($tx->amount, 2) . ' Dorito$'; // Assuming USD as your currency
        ?>               
        
        <tr class="<?php echo $zeroAmountClass ?>">
            <td><?php echo esc_html(date('m-d-Y H:i', $tx->time)); ?></td>
            <td><?php echo $sender; ?></td>
            <td><?php echo $receiver; ?></td>
            <td><?php echo esc_html($formattedAmount); ?></td>
            <td><?php echo esc_html($tx->description); ?></td>
        </tr>
        <?php endforeach; ?>
    </tbody>
</table>
<?php else: ?>
    <p>No transactions found.</p>
<?php endif; ?>